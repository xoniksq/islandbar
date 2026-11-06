using System.Text.Json;

namespace IslandBar;

sealed record DownloadItem(string Key,string Name,string Target,string Current,string Browser,long Received,long Total,int State,DateTime Started,bool Observed=false)
{
    public bool Active=>State==0;
    public double? Percent=>Total>0?Math.Clamp(Received*100d/Total,0,100):null;
}
sealed class DownloadMonitor
{
    readonly Dictionary<string,DownloadItem> _known=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,DateTime> _seenActive=new(StringComparer.OrdinalIgnoreCase);
    (string File,string Name)[]? _profiles;
    DateTime _discovered;
    readonly DateTime _started=DateTime.UtcNow;
    bool _initialized;
    readonly bool _fixedProfiles;
    readonly string[]? _watchDirectories;
    public DownloadMonitor((string File,string Name)[]? profiles=null,string[]? watchDirectories=null){_profiles=profiles;_fixedProfiles=profiles is not null;_watchDirectories=watchDirectories;}
    public int SourcesAvailable {get;private set;}
    public IReadOnlyList<DownloadItem> Current {get;private set;}=[];
    public List<DownloadItem> Read()
    {
        if(_profiles is null||(!_fixedProfiles&&DateTime.UtcNow-_discovered>TimeSpan.FromMinutes(2)))Discover();
        var found=new List<DownloadItem>();int available=0;
        long cutoff=DateTime.UtcNow.AddDays(-2).ToFileTimeUtc()/10;
        foreach(var profile in _profiles!)try{
            var rows=SqliteRead.Query(profile.File,$"SELECT id,current_path,target_path,received_bytes,total_bytes,state,start_time FROM downloads WHERE state=0 OR start_time>={cutoff} ORDER BY id DESC LIMIT 40",r=>{
                string current=r.Text(1),target=r.Text(2);if(string.IsNullOrWhiteSpace(target))target=current;
                return new DownloadItem(profile.File+"|"+r.Number(0),Path.GetFileName(target),target,current,profile.Name,Math.Max(0,r.Number(3)),r.Number(4),(int)r.Number(5),FromChromeTime(r.Number(6)));
            });available++;
            foreach(var row in rows){
                bool observed=_known.TryGetValue(row.Key,out var previous)&&previous.Active;
                if(row.Active){
                    if(row.Started<DateTime.UtcNow.AddDays(-1))continue;
                    _seenActive[row.Key]=DateTime.UtcNow;
                }
                found.Add(row with {Observed=observed||previous?.Observed==true||(_initialized&&!_known.ContainsKey(row.Key)&&row.Started>=_started)});
            }
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        foreach(string directory in (_watchDirectories??[QuickFolders.Downloads]).Concat(found.Select(f=>Path.GetDirectoryName(f.Target)??"")).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))try{
            foreach(string path in Directory.EnumerateFiles(directory).Where(p=>p.EndsWith(".crdownload",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".part",StringComparison.OrdinalIgnoreCase)).Take(50)){
                var file=new FileInfo(path);if(DateTime.UtcNow-file.LastWriteTimeUtc>TimeSpan.FromMinutes(2))continue;
                int existing=found.FindIndex(f=>f.Active&&(f.Current.Equals(path,StringComparison.OrdinalIgnoreCase)||path.Equals(f.Target+".crdownload",StringComparison.OrdinalIgnoreCase)||path.Equals(f.Target+".part",StringComparison.OrdinalIgnoreCase)));
                if(existing>=0){var row=found[existing];if(row.Total<=0||file.Length<row.Total)found[existing]=row with {Received=Math.Max(row.Received,file.Length)};continue;}
                string name=Path.GetFileNameWithoutExtension(path),key="file:"+path;_seenActive[key]=DateTime.UtcNow;
                found.Add(new(key,name,Path.Combine(directory,name),path,"Загрузка файла",file.Length,0,0,file.CreationTimeUtc));
            }
        }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
        foreach(var previous in _known.Values.Where(d=>d.Active)){
            if(found.Any(f=>f.Key==previous.Key))continue;
            if(previous.Key.StartsWith("file:",StringComparison.Ordinal)&&File.Exists(previous.Target)&&!File.Exists(previous.Current))found.Add(previous with {State=1,Received=new FileInfo(previous.Target).Length,Observed=true});
            else if(_seenActive.TryGetValue(previous.Key,out var seen)&&DateTime.UtcNow-seen<TimeSpan.FromSeconds(8))found.Add(previous);
        }
        foreach(var previous in _known.Values.Where(d=>d.Key.StartsWith("file:",StringComparison.Ordinal)&&d.State==1&&File.Exists(d.Target)&&DateTime.UtcNow-d.Started<TimeSpan.FromDays(2)))if(found.All(f=>f.Key!=previous.Key))found.Add(previous);
        SourcesAvailable=available;_initialized=true;_known.Clear();foreach(var row in found)_known[row.Key]=row;
        Current=found.OrderByDescending(d=>d.Active).ThenByDescending(d=>d.Started).Take(12).ToArray();return Current.ToList();
    }
    void Discover()
    {
        _discovered=DateTime.UtcNow;var result=new List<(string,string)>();string local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach(var (root,name) in new[]{(Path.Combine(local,@"Yandex\YandexBrowser\User Data"),"Яндекс Браузер"),(Path.Combine(local,@"Google\Chrome\User Data"),"Chrome"),(Path.Combine(local,@"Microsoft\Edge\User Data"),"Edge"),(Path.Combine(roaming,@"Opera Software\Opera GX Stable"),"Opera GX"),(Path.Combine(roaming,@"Opera Software\Opera Stable"),"Opera")}){
            if(!Directory.Exists(root))continue;
            if(File.Exists(Path.Combine(root,"History")))result.Add((Path.Combine(root,"History"),name));
            foreach(string dir in Directory.EnumerateDirectories(root).Where(d=>Path.GetFileName(d)=="Default"||Path.GetFileName(d).StartsWith("Profile ",StringComparison.Ordinal)).Take(12))if(File.Exists(Path.Combine(dir,"History")))result.Add((Path.Combine(dir,"History"),name));
        }
        _profiles=result.ToArray();
    }
    static DateTime FromChromeTime(long micros){try{return DateTime.FromFileTimeUtc(checked(micros*10));}catch{return DateTime.MinValue;}}
}
