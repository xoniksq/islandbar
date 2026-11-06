using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace IslandBar;

static class Program
{
    public static readonly string Folder = Path.GetDirectoryName(Environment.ProcessPath!)!;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RestoreEvent = @"Local\IslandBar.Restore";
    public const string ExpandEvent = @"Local\IslandBar.Expand";
    public const string CollapseEvent = @"Local\IslandBar.Collapse";
    public const string StorageEvent = @"Local\IslandBar.Storage";
    static string Journal => Path.Combine(Folder, "session.json");
    public sealed record Session(int Owner, bool OriginalVisible, bool Active, Native.TaskbarPresentation? TaskbarPresentation=null,
        Native.OverflowPresentation? OverflowPresentation=null);

    [STAThread]
    static int Main(string[] args)
    {
        Directory.CreateDirectory(Folder);
        if (args is ["--apply-update", var owner] && int.TryParse(owner, out int updateOwner))
            return IslandUpdater.Apply(updateOwner);
        if (args.Contains("--hide-desktop-icons")) return DesktopIcons.SetHidden(true) ? 0 : 1;
        if (args.Contains("--show-desktop-icons")) return DesktopIcons.SetHidden(false) ? 0 : 1;
        if(args.Contains("--shell-key-check")){
            try{ShellKeyCheck.Run();return 0;}catch(Exception ex){Log("shell key check: "+ex.Message);return 1;}
        }
        if(args.Contains("--convenience-check")){try{ConvenienceCheck.Run();return 0;}catch(Exception ex){Log("convenience check: "+ex);return 1;}}
        if(args.Contains("--extras-check")){try{ExtrasCheck.Run();return 0;}catch(Exception ex){Log("extras check: "+ex);return 1;}}
        if(args.Contains("--extras-diagnostics")){
            try{var monitor=new HardwareMonitor();monitor.Read();Thread.Sleep(200);var hardware=monitor.Read();var downloads=new DownloadMonitor();var items=downloads.Read();File.WriteAllText(Path.Combine(Folder,"extras-diagnostics.json"),JsonSerializer.Serialize(new {Hardware=hardware,Downloads=items,Sources=downloads.SourcesAvailable,Folders=QuickFolders.All()},new JsonSerializerOptions {WriteIndented=true}));return 0;}catch(Exception ex){Log("extras diagnostics: "+ex);return 1;}
        }
        if(args.Contains("--apply-style")||args.Contains("--restore-style")){
            try{if(args.Contains("--apply-style"))DesktopStyle.Apply();else DesktopStyle.Restore();return 0;}catch(Exception ex){Log("appearance: "+ex);return 1;}
        }
        if(args.Contains("--feature-check")||args.Contains("--feature-diagnostics")){
            var checkApp=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            checkApp.Startup+=async(_,_)=>{try{await FeatureCheck.RunAsync(args.Contains("--feature-check"));checkApp.Shutdown(0);}catch(Exception ex){Log("feature check: "+ex);checkApp.Shutdown(1);}};
            return checkApp.Run();
        }
        if (args.Length >= 2 && args[0] == "--watch" && int.TryParse(args[1], out int pid)) return Watch(pid);
        if(args.Contains("--configure-startup")){SetAutostart(true);return 0;}
        if (args.Contains("--restore"))
        {
            SetAutostart(false);
            if (EventWaitHandle.TryOpenExisting(RestoreEvent, out var signal))
                using (signal) signal.Set();
            else Restore();
            return 0;
        }
        if (args.Contains("--collapse"))
        {
            if (EventWaitHandle.TryOpenExisting(CollapseEvent, out var signal)) using (signal) signal.Set();
            return 0;
        }
        using var mutex = new Mutex(true, @"Local\IslandBar.SingleInstance", out bool first);
        if (!first)
        {
            string name = args.Contains("--files") ? StorageEvent : ExpandEvent;
            if (EventWaitHandle.TryOpenExisting(name, out var signal)) using (signal) signal.Set();
            return 0;
        }
        if (args.Contains("--diagnostics"))
        {
            var apps = new Catalog().GetApps();
            File.WriteAllText(Path.Combine(Folder, "diagnostics.json"), JsonSerializer.Serialize(new {
                TaskbarVisible = Native.IsTaskbarVisible,
                Apps = apps.Select(a => new { a.Name, a.Key, Windows = a.Windows.Count, HasIcon = a.Icon is not null })
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        bool preview = args.Contains("--preview") || args.Contains("--animation-check");
        try
        {
            if (!preview)
            {
                if(!args.Contains("--inspect"))Startup.EnsureReturnShortcut();
                Restore();
                WriteSession(new(Environment.ProcessId, Native.IsTaskbarVisible, true));
                using var watchdog = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--watch " + Environment.ProcessId) {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                });
                if (watchdog is null) throw new InvalidOperationException("Не удалось запустить восстановление панели задач");
                if (args.Contains("--enable-startup")) SetAutostart(true);
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) => { Log(e.Exception.ToString()); Restore(); e.Handled = true; app.Shutdown(1); };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log(e.ExceptionObject.ToString() ?? "fatal"); Restore(); };
            var window = new IslandWindow(preview,args.Contains("--inspect"));
            if(args.Contains("--return"))window.Loaded+=(_,_)=>window.Expand();
            if(args.Contains("--files"))window.Loaded+=(_,_)=>window.OpenStorage();
            if(args.Contains("--animation-check")) window.Loaded += async (_, _) => await window.CheckAnimationAsync();
            app.Exit += (_, _) => { if (!preview) Restore(); };
            Log("start " + (preview ? "preview" : "active"));
            return app.Run(window);
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            if (!preview) Restore();
            return 1;
        }
    }

    static int Watch(int pid)
    {
        try { using var parent = Process.GetProcessById(pid); parent.WaitForExit(); }
        catch (ArgumentException) { }
        catch (Exception ex) { Log("watch: " + ex.Message); return 1; }
        try
        {
            var state = ReadSession();
            if (state is { Active: true } && state.Owner == pid) { Restore(); Log("watchdog restored taskbar"); }
        }
        catch (Exception ex) { Log("watch restore: " + ex.Message); Native.SetTaskbarVisible(true); }
        return 0;
    }

    static Session? ReadSession()
    {
        try { return File.Exists(Journal) ? JsonSerializer.Deserialize<Session>(File.ReadAllText(Journal)) : null; }
        catch { return null; }
    }
    static void WriteSession(Session state)
    {
        var temp = Journal + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, Journal, true);
    }
    public static void Restore()
    {
        var state = ReadSession();
        if (state is { Active: true })
        {
            if(state.OverflowPresentation is {} overflow)Native.RestoreOverflowPresentation(overflow);
            if(state.TaskbarPresentation is {} saved)Native.RestoreTaskbarPresentation(saved);
            Native.SetTaskbarVisible(state.OriginalVisible);
            WriteSession(state with { Active = false,TaskbarPresentation=null,OverflowPresentation=null });
        }
        else if (state is null) Native.SetTaskbarVisible(true);
    }
    public static void SaveTaskbarPresentation(Native.TaskbarPresentation? saved)
    {
        if(ReadSession() is {Active:true} state&&state.Owner==Environment.ProcessId)WriteSession(state with {TaskbarPresentation=saved});
    }
    public static void SaveOverflowPresentation(Native.OverflowPresentation? saved)
    {
        if(ReadSession() is {Active:true} state&&state.Owner==Environment.ProcessId)WriteSession(state with {OverflowPresentation=saved});
    }
    public static void SetAutostart(bool enabled)
    {
        Startup.Configure(enabled);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.DeleteValue("IslandBar", false);
    }
    public static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(Folder, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}"); }
        catch { }
    }
}
