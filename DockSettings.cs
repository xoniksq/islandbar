using System.Text.Json;

namespace IslandBar;

sealed record StoredApp(string Key,string Name,string Path,string Arguments,string? WorkingDirectory)
{
    public static StoredApp? From(DockApp app)=>app.LaunchPath is null?null:new(app.Key,FriendlyName(app),app.LaunchPath,app.Arguments,app.WorkingDirectory);
    static string FriendlyName(DockApp app)=>System.IO.Path.GetFileNameWithoutExtension(app.LaunchPath??"").ToLowerInvariant() switch{"chrome"=>"Google Chrome","msedge"=>"Microsoft Edge","яндекс музыка"=>"Яндекс.Музыка",_=>app.Name};
    public DockApp ToApp()=>new(){Key=Key,Name=Name,LaunchPath=Path,Arguments=Arguments,WorkingDirectory=WorkingDirectory,Pinned=true};
    public static StoredApp FromFile(string path)
    {
        string target=path,args="";object? shell=null,link=null;
        try{if(System.IO.Path.GetExtension(path).Equals(".lnk",StringComparison.OrdinalIgnoreCase)){shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);dynamic wsh=shell!;link=wsh.CreateShortcut(path);dynamic shortcut=link;string resolved=shortcut.TargetPath;if(!string.IsNullOrWhiteSpace(resolved)){target=resolved;args=shortcut.Arguments;}}}
        finally{if(link is not null)System.Runtime.InteropServices.Marshal.ReleaseComObject(link);if(shell is not null)System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);}
        return new(Catalog.GroupKey(target,args),System.IO.Path.GetFileNameWithoutExtension(path),path,"",System.IO.Path.GetDirectoryName(path));
    }
}
sealed record DockSettings(List<StoredApp> Pins,List<string> Hidden,List<string> Order)
{
    static string FilePath=>System.IO.Path.Combine(Program.Folder,"dock-layout.json");
    public static DockSettings Load(){try{return File.Exists(FilePath)?JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(FilePath))??new([],[],[]):new([],[],[]);}catch{return new([],[],[]);}}
    public void Save(){string temp=FilePath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,FilePath,true);}
    public static void Toggle(DockApp app){var settings=Load();settings.Pins.RemoveAll(p=>p.Key==app.Key);settings.Hidden.RemoveAll(k=>k==app.Key);if(app.Pinned)settings.Hidden.Add(app.Key);else if(StoredApp.From(app) is {} stored)settings.Pins.Add(stored);settings.Save();}
    public static void SaveOrder(IEnumerable<string> order){var settings=Load();settings.Order.Clear();settings.Order.AddRange(order.Distinct(StringComparer.OrdinalIgnoreCase));settings.Save();}
}

sealed record AppSet(string Name,List<StoredApp> Apps);
static class AppSets
{
    static string FilePath=>Path.Combine(Program.Folder,"app-sets.json");
    public static List<AppSet> Load()
    {
        try{
            if(!File.Exists(FilePath))return [];
            var sets=JsonSerializer.Deserialize<List<AppSet>>(File.ReadAllText(FilePath))??[];
            return sets.Where(set=>!string.IsNullOrWhiteSpace(set?.Name)&&set.Apps is {Count:>0}
                &&set.Apps.All(app=>!string.IsNullOrWhiteSpace(app?.Key)&&!string.IsNullOrWhiteSpace(app.Name)
                    &&!string.IsNullOrWhiteSpace(app.Path))).ToList();
        }
        catch{return [];}
    }
    public static void Save(IEnumerable<AppSet> sets){string temp=FilePath+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(sets,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,FilePath,true);}
    public static bool IsRunning(StoredApp app,IReadOnlyList<DockApp> running)=>running.Any(a=>a.Key.Equals(app.Key,StringComparison.OrdinalIgnoreCase)&&a.Windows.Any(Native.IsWindow));
}
