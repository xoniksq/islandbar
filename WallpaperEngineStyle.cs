using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace IslandBar;

static class WallpaperEngineStyle
{
    sealed record Snapshot(string Exe,Dictionary<string,string> Wallpapers);
    static string Backup=>Path.Combine(Program.Folder,"wallpaper-engine-before-island.json");
    static string? RunningExe()
    {
        foreach(string name in new[]{"wallpaper64","wallpaper32"})foreach(var process in Process.GetProcessesByName(name))using(process){string? path=Native.ProcessPath((uint)process.Id);if(path is not null)return path;}
        return null;
    }
    public static bool Apply(string bitmap)
    {
        string? exe=RunningExe();if(exe is null)return false;
        if(!File.Exists(Backup)){
            string config=Path.Combine(Path.GetDirectoryName(exe)!,"config.json");
            using var json=JsonDocument.Parse(File.ReadAllText(config));
            var selected=json.RootElement.GetProperty(Environment.UserName).GetProperty("general").GetProperty("wallpaperconfig").GetProperty("selectedwallpapers");
            var wallpapers=selected.EnumerateObject().Where(p=>p.Value.TryGetProperty("file",out _)).ToDictionary(p=>p.Name,p=>p.Value.GetProperty("file").GetString()!);
            if(wallpapers.Count==0)throw new InvalidOperationException("Не удалось сохранить текущие обои Wallpaper Engine");
            File.WriteAllText(Backup,JsonSerializer.Serialize(new Snapshot(exe,wallpapers),new JsonSerializerOptions {WriteIndented=true}));
        }
        var saved=JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Backup))!;
        string folder=Path.Combine(Program.Folder,"Graphite Blue Wallpaper");Directory.CreateDirectory(folder);
        using(var input=File.OpenRead(bitmap)){
            var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);var encoder=new PngBitmapEncoder();encoder.Frames.Add(decoder.Frames[0]);using var output=File.Create(Path.Combine(folder,"graphite.png"));encoder.Save(output);
        }
        File.WriteAllText(Path.Combine(folder,"index.html"),"<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{width:100%;height:100%;margin:0;overflow:hidden;background:#141a24 url('graphite.png') center/cover no-repeat}</style></head><body></body></html>");
        string project=Path.Combine(folder,"project.json");File.WriteAllText(project,JsonSerializer.Serialize(new {title="Graphite Blue · IslandBar",type="web",file="index.html",preview="graphite.png",description="Графитовый фон с мягким голубым акцентом. Локальные файлы, без подключения к сети."}));
        foreach(string location in saved.Wallpapers.Keys)Load(exe,project,location);
        return true;
    }
    public static void Restore()
    {
        if(!File.Exists(Backup)||RunningExe() is null)return;
        var saved=JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(Backup));if(saved is null)return;
        foreach(var wallpaper in saved.Wallpapers)if(File.Exists(wallpaper.Value))Load(saved.Exe,wallpaper.Value,wallpaper.Key);
    }
    static void Load(string exe,string file,string location)
    {
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        foreach(string arg in new[]{"-control","openWallpaper","-file",file,"-location",location})start.ArgumentList.Add(arg);
        using var process=Process.Start(start);if(process is null||!process.WaitForExit(5000))throw new InvalidOperationException("Wallpaper Engine не завершил команду смены обоев");
        if(process.ExitCode!=0)throw new InvalidOperationException("Wallpaper Engine отклонил смену обоев: "+process.ExitCode);
    }
}
