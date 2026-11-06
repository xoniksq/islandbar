using System.Text.Json;
namespace IslandBar;
static class ConvenienceCheck
{
    public static void Run()
    {
        var checks=new Dictionary<string,bool>();var network=new NetworkMonitor();network.Read();Thread.Sleep(250);var rate=network.Read();checks["NetworkCounters"]=double.IsFinite(rate.Down)&&double.IsFinite(rate.Up)&&rate.Down>=0&&rate.Up>=0&&rate.Adapters.Length>0;
        var mixer=AppMixer.Read();checks["MixerSessions"]=mixer.Length>0&&mixer.All(a=>a.Volume is >=0 and <=100);checks["MixerWriteReadRestore"]=AppMixer.Probe();
        string steamLink=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\Steam.lnk");
        if(File.Exists(steamLink))checks["ShortcutLaunchKey"]=StoredApp.FromFile(steamLink).Key=="app:steam";
        string dock=Path.Combine(Program.Folder,"dock-layout.json"),sets=Path.Combine(Program.Folder,"app-sets.json");string? oldDock=File.Exists(dock)?File.ReadAllText(dock):null,oldSets=File.Exists(sets)?File.ReadAllText(sets):null;
        try{
            var app=new DockApp{Name="Check",Key="check",LaunchPath=Path.Combine(Environment.SystemDirectory,"notepad.exe")};DockSettings.Toggle(app);checks["PinPersists"]=DockSettings.Load().Pins.Any(p=>p.Key=="check");
            DockSettings.SaveOrder(["B","A"]);checks["OrderPersists"]=DockSettings.Load().Order.SequenceEqual(new[]{"B","A"});
            DockSettings.Toggle(new DockApp{Name=app.Name,Key=app.Key,LaunchPath=app.LaunchPath,Pinned=true});checks["UnpinPersists"]=DockSettings.Load().Pins.All(p=>p.Key!="check")&&DockSettings.Load().Hidden.Contains("check");
            var stored=StoredApp.From(app)!;AppSets.Save([new AppSet("Проверка",[stored])]);checks["AppSetPersists"]=AppSets.Load().Single().Apps.Single().Key=="check";
            var running=new DockApp{Name=app.Name,Key=app.Key,LaunchPath=app.LaunchPath};running.Windows.Add(Native.GetShellWindow());checks["AppSetSkipsOpenApps"]=AppSets.IsRunning(stored,[running])&&!AppSets.IsRunning(stored,[]);
        }finally{if(oldDock is null)File.Delete(dock);else File.WriteAllText(dock,oldDock);if(oldSets is null)File.Delete(sets);else File.WriteAllText(sets,oldSets);}
        File.WriteAllText(Path.Combine(Program.Folder,"convenience-check.json"),JsonSerializer.Serialize(new{Success=checks.Values.All(v=>v),Checks=checks,Network=rate,Mixer=mixer},new JsonSerializerOptions{WriteIndented=true}));
    }
}
