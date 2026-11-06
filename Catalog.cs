using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace IslandBar;

sealed class DockApp
{
    public required string Name { get; init; }
    public required string Key { get; init; }
    public string? LaunchPath { get; init; }
    public string Arguments { get; init; } = "";
    public string? WorkingDirectory { get; init; }
    public ImageSource? Icon { get; init; }
    public bool Pinned {get;init;}
    public List<nint> Windows { get; } = [];
}

sealed class Catalog
{
    readonly List<DockApp> _pins = [];
    readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    public Catalog()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        if (!Directory.Exists(folder)) return;
        object? shellObject = null;
        try
        {
            shellObject = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic shell = shellObject!;
            foreach (var link in Directory.EnumerateFiles(folder, "*.lnk").OrderBy(Path.GetFileName))
            {
                object? shortcutObject = null;
                try
                {
                    shortcutObject = shell.CreateShortcut(link);
                    dynamic shortcut = shortcutObject;
                    string target = shortcut.TargetPath;
                    string args = shortcut.Arguments;
                    string directory = shortcut.WorkingDirectory;
                    string name = Path.GetFileNameWithoutExtension(link);
                    if (string.IsNullOrWhiteSpace(target)) {
                        if (name.Contains("Explorer", StringComparison.OrdinalIgnoreCase) || name.Contains("Проводник", StringComparison.OrdinalIgnoreCase))
                            target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                        else continue;
                    }
                    string key = GroupKey(target, args);
                    if (_pins.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                    string iconPath=File.Exists(target)?target:link;
                    if(key=="app:discord") {
                        string? root=Path.GetDirectoryName(target);
                        if(root is not null&&Directory.Exists(root)) iconPath=Directory.EnumerateDirectories(root,"app-*")
                            .OrderByDescending(p=>Path.GetFileName(p),StringComparer.OrdinalIgnoreCase)
                            .Select(p=>Path.Combine(p,"Discord.exe")).FirstOrDefault(File.Exists)??iconPath;
                    }
                    if(key=="app:explorer")name="Проводник";
                    _pins.Add(new DockApp {
                        Name = name, Key = key,
                        LaunchPath = target, Arguments = args, WorkingDirectory = directory,
                        Icon = GetIcon(iconPath)
                    });
                }
                catch (Exception ex) { Program.Log("pin: " + ex.Message); }
                finally { if (shortcutObject is not null) Marshal.FinalReleaseComObject(shortcutObject); }
            }
        }
        finally { if (shellObject is not null) Marshal.FinalReleaseComObject(shellObject); }
        static int Rank(DockApp app)=>app.Key switch {"app:steam"=>1,"app:explorer"=>3,"app:discord"=>4,_=>app.Name switch{"Telegram"=>0,"Yandex"=>2,_=>100}};
        _pins.Sort((a,b)=>Rank(a).CompareTo(Rank(b)));
    }

    public static string GroupKey(string path, string arguments = "")
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name is "steam" or "steamwebhelper") return "app:steam";
        if (name == "discord" || (name == "update" && arguments.Contains("Discord.exe",StringComparison.OrdinalIgnoreCase))) return "app:discord";
        if (name == "explorer") return "app:explorer";
        return path;
    }

    ImageSource? GetIcon(string path)
    {
        if (!_icons.TryGetValue(path, out var icon)) _icons[path] = icon = Native.Icon(path);
        return icon;
    }

    public List<DockApp> GetApps()
    {
        var settings=DockSettings.Load();
        var apps = _pins.Where(p=>!settings.Hidden.Contains(p.Key,StringComparer.OrdinalIgnoreCase)).Select(p => new DockApp { Name=p.Name, Key=p.Key, LaunchPath=p.LaunchPath,
            Arguments=p.Arguments, WorkingDirectory=p.WorkingDirectory, Icon=p.Icon,Pinned=true }).ToList();
        foreach(var pin in settings.Pins)if(apps.All(a=>!a.Key.Equals(pin.Key,StringComparison.OrdinalIgnoreCase)))apps.Add(new DockApp{Name=pin.Name,Key=pin.Key,LaunchPath=pin.Path,Arguments=pin.Arguments,WorkingDirectory=pin.WorkingDirectory,Icon=GetIcon(pin.Path),Pinned=true});
        Native.EnumWindows((window, _) => {
            try
            {
                if (!Native.IsWindowVisible(window) || Native.GetWindow(window, 4) != 0 || Native.IsCloaked(window)) return true;
                if (((long)Native.GetWindowLong(window, -20) & 0x80) != 0) return true;
                string title = Native.Title(window);
                if (string.IsNullOrWhiteSpace(title)) return true;
                string cls = Native.ClassName(window);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                Native.GetWindowThreadProcessId(window, out uint pid);
                if (pid == Environment.ProcessId) return true;
                using var process = Process.GetProcessById((int)pid);
                string? path = Native.ProcessPath(pid);
                string name = process.ProcessName;
                if (name is "SearchHost" or "StartMenuExperienceHost" or "ShellExperienceHost" or "TextInputHost") return true;
                string key = path is null ? "pid:" + pid : GroupKey(path);
                var app = apps.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (app is null)
                {
                    var original=_pins.FirstOrDefault(p=>p.Key.Equals(key,StringComparison.OrdinalIgnoreCase));
                    app = new DockApp { Name = original?.Name??title, Key = key, LaunchPath = original?.LaunchPath??path,Arguments=original?.Arguments??"",WorkingDirectory=original?.WorkingDirectory,
                        Icon = original?.Icon??(path is null ? null : GetIcon(path)) };
                    apps.Add(app);
                }
                app.Windows.Add(window);
            }
            catch { /* An app may close while its windows are enumerated. */ }
            return true;
        }, 0);
        return apps.OrderBy(a=>{int index=settings.Order.FindIndex(k=>k.Equals(a.Key,StringComparison.OrdinalIgnoreCase));return index<0?int.MaxValue:index;}).ToList();
    }

    public static void Launch(DockApp app)
    {
        if (app.LaunchPath is null) return;
        var start = new ProcessStartInfo(app.LaunchPath) { UseShellExecute = true, Arguments = app.Arguments };
        if (!string.IsNullOrWhiteSpace(app.WorkingDirectory) && Directory.Exists(app.WorkingDirectory)) start.WorkingDirectory = app.WorkingDirectory;
        Process.Start(start)?.Dispose();
    }

    public static void OpenFavorite(DockApp app)
    {
        nint window=app.Windows.FirstOrDefault(Native.IsWindow);
        if(window!=0)Native.Activate(window);
        else Launch(app);
    }
}
