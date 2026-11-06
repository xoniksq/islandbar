using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace IslandBar;

static class ExtrasCheck
{
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)]string file,out nint db,int flags,nint vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern int sqlite3_exec(nint db,[MarshalAs(UnmanagedType.LPUTF8Str)]string sql,nint callback,nint state,nint error);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.StdCall)]static extern int sqlite3_close(nint db);
    static string Sql(string text)=>"'"+text.Replace("'","''")+"'";
    public static void Run()
    {
        var checks=new Dictionary<string,bool>();string tempRoot=Path.GetFullPath(Path.GetTempPath());string folder=Path.Combine(tempRoot,"IslandBarCheck-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);nint db=0;
        try{
            string history=Path.Combine(folder,"History"),current=Path.Combine(folder,"test.zip.crdownload"),target=Path.Combine(folder,"test.zip");
            if(sqlite3_open_v2(history,out db,6,0)!=0)throw new IOException("Fixture database could not open");
            void Exec(string sql){if(sqlite3_exec(db,sql,0,0,0)!=0)throw new IOException("Fixture write failed");}
            Exec("CREATE TABLE downloads(id INTEGER PRIMARY KEY,current_path TEXT,target_path TEXT,received_bytes INTEGER,total_bytes INTEGER,state INTEGER,start_time INTEGER);");
            File.WriteAllBytes(current,new byte[32768]);long started=DateTime.UtcNow.ToFileTimeUtc()/10;
            Exec($"INSERT INTO downloads VALUES(1,{Sql(current)},{Sql(target)},32768,65536,0,{started});");
            var monitor=new DownloadMonitor([(history,"Проверка")],[folder]);var first=monitor.Read();
            checks["KnownDownloadProgress"]=first.Count==1&&first[0].Active&&first[0].Percent==50;
            File.WriteAllBytes(current,new byte[49152]);Exec("UPDATE downloads SET received_bytes=49152 WHERE id=1;");var progressing=monitor.Read();
            checks["ProgressUpdates"]=progressing.Count==1&&progressing[0].Percent==75;
            File.Move(current,target);Exec("UPDATE downloads SET received_bytes=65536,state=1 WHERE id=1;");var finished=monitor.Read();
            checks["CompletionDetected"]=finished.Count==1&&finished[0].State==1&&finished[0].Observed;
            string unknown=Path.Combine(folder,"unknown.txt.part");File.WriteAllBytes(unknown,new byte[1024]);var partial=monitor.Read();
            checks["UnknownSizeDownload"]=partial.Any(d=>d.Current==unknown&&d.Active&&d.Total==0&&d.Percent is null);
            File.Move(unknown,Path.Combine(folder,"unknown.txt"));var renamed=monitor.Read();
            checks["PartialRenameCompletion"]=renamed.Any(d=>d.Name=="unknown.txt"&&d.State==1&&d.Observed);
            var retained=monitor.Read();checks["CompletedFileRetained"]=retained.Any(d=>d.Name=="unknown.txt"&&d.State==1);
            string Hash(){using var stream=new FileStream(history,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return Convert.ToHexString(SHA256.HashData(stream));}
            string before=Hash();monitor.Read();string after=Hash();checks["HistoryReadOnly"]=before==after;
            var gate=new DragDwell();var now=DateTime.UtcNow;
            checks["DragNoImmediateSwitch"]=!gate.ShouldPreview("A",now)&&!gate.ShouldPreview("A",now.AddMilliseconds(500));
            checks["DragDelayedSwitch"]=gate.ShouldPreview("A",now.AddMilliseconds(700))&&!gate.ShouldPreview("A",now.AddMilliseconds(900));
            checks["DragChangingTargetResetsDelay"]=!gate.ShouldPreview("B",now.AddMilliseconds(1000))&&!gate.ShouldPreview("B",now.AddMilliseconds(1500))&&gate.ShouldPreview("B",now.AddMilliseconds(1700));
            var hardware=new HardwareMonitor();hardware.Read();Thread.Sleep(200);var pc=hardware.Read();
            checks["CpuAndMemory"]=pc.CpuLoad is >=0 and <=100&&pc.RamTotalGb>0&&pc.RamUsedGb>=0&&pc.RamUsedGb<=pc.RamTotalGb;
            checks["TemperatureReadings"]=pc.CpuTemperature is >0 and <150&&pc.GpuTemperature is >0 and <150;
            var folders=QuickFolders.All();checks["KnownFolders"]=Directory.Exists(QuickFolders.Downloads)&&folders.All(f=>Directory.Exists(f.Path));
            File.WriteAllText(Path.Combine(Program.Folder,"extras-check.json"),JsonSerializer.Serialize(new {Success=checks.Values.All(v=>v),Checks=checks,Hardware=new {pc.CpuLoad,pc.CpuTemperature,pc.GpuLoad,pc.GpuTemperature,pc.RamUsedGb,pc.RamTotalGb,pc.SensorSource},Folders=folders.Length},new JsonSerializerOptions {WriteIndented=true}));
        }finally{
            if(db!=0)sqlite3_close(db);
            string resolved=Path.GetFullPath(folder);
            if(Path.GetDirectoryName(resolved)==Path.TrimEndingDirectorySeparator(tempRoot)&&Path.GetFileName(resolved).StartsWith("IslandBarCheck-",StringComparison.Ordinal))Directory.Delete(resolved,true);
        }
    }
}
