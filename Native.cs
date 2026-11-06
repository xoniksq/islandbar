using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar;

static class Native
{
    public delegate bool EnumProc(nint window, nint state);
    public delegate nint MouseProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] public struct MouseData { public Point Position; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileInfo { public nint Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint FindWindow(string? className, string? title);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, nint state);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] public static extern nint GetShellWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLong(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLong(nint window, int index, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] public static extern bool PostMessage(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint window,nint insertAfter,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(nint window,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(nint window,out uint key,out byte alpha,out uint flags);
    [DllImport("user32.dll")] public static extern nint GetKeyboardLayout(uint thread);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, uint size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rect value, uint size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern nint SHGetFileInfo(string path, uint attributes, ref FileInfo info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] public static extern nint SetWindowsHookEx(int type, MouseProc callback, nint module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] public static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern nint GetModuleHandle(string? module);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder text, ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);
    public static bool IsPackaged { get { uint length=0; return GetCurrentPackageFullName(ref length,null) != 15700; } }
    public static bool FullscreenBusy(nint dock, ref nint fullscreenWindow)
    {
        nint monitor = MonitorFromWindow(dock, 2);
        nint foreground = GetForegroundWindow();
        if (IsApplicationWindow(foreground) && MonitorFromWindow(foreground, 0) == monitor)
            fullscreenWindow = CoversMonitor(foreground, monitor) ? foreground : 0;
        else if (fullscreenWindow != 0 && !CoversMonitor(fullscreenWindow, monitor))
            fullscreenWindow = 0;

        return fullscreenWindow != 0 || SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4;
    }

    public static bool IsApplicationWindow(nint window)
    {
        if (window == 0 || window == GetShellWindow()) return false;
        GetWindowThreadProcessId(window, out uint pid);
        return pid != 0 && pid != Environment.ProcessId &&
            ClassName(window) is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow");
    }

    public static (nint Id, Rect Bounds, Rect Work) PrimaryDisplay() => DisplayInfo(MonitorFromPoint(default, 1));
    public static (nint Id, Rect Bounds, Rect Work) Display(nint window) => DisplayInfo(MonitorFromWindow(window, 2));

    static (nint Id, Rect Bounds, Rect Work) DisplayInfo(nint id)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(id, ref info) ? (id, info.Monitor, info.Work) : default;
    }

    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint window);

    static bool CoversMonitor(nint window, nint monitor)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || IsCloaked(window) || MonitorFromWindow(window, 0) != monitor) return false;
        const long caption = 0x00C00000, resizeFrame = 0x00040000;
        long style = (long)GetWindowLong(window, -16);
        // Chromium can retain WS_MAXIMIZE in fullscreen, while removing the caption and resize frame.
        if (IsZoomed(window) && ((style & caption) == caption || (style & resizeFrame) != 0)) return false;
        if (DwmGetWindowAttribute(window, 9, out Rect rect, 16) != 0 &&
            !GetWindowRect(window, out rect)) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        const int pixelTolerance = 2;
        return rect.Left <= info.Monitor.Left + pixelTolerance && rect.Top <= info.Monitor.Top + pixelTolerance &&
            rect.Right >= info.Monitor.Right - pixelTolerance && rect.Bottom >= info.Monitor.Bottom - pixelTolerance;
    }

    public static string? ProcessPath(uint pid)
    {
        nint process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try {
            var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        } finally { CloseHandle(process); }
    }

    public static void SetTaskbarVisible(bool visible)
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != 0 && IsWindowVisible(taskbar) != visible) ShowWindow(taskbar, visible ? 8 : 0);
    }

    public sealed record TaskbarPresentation(long Style,uint Key,byte Alpha,uint Flags);
    public sealed record OverflowPresentation(long Handle,TaskbarPresentation Style);

    public static nint TrayOverflowWindow()
    {
        nint window=FindWindow("TopLevelWindowForOverflowXamlIsland",null);
        return window!=0?window:FindWindow("NotifyIconOverflowWindow",null);
    }

    public static OverflowPresentation MaskOverflow(nint window)
    {
        var saved=new OverflowPresentation((long)window,ReadPresentation(window));
        Program.SaveOverflowPresentation(saved);
        if(MakeTransparent(window,saved.Style))return saved;
        RestoreOverflowPresentation(saved);
        Program.SaveOverflowPresentation(null);
        throw new InvalidOperationException("Не удалось скрыть системное окно значков");
    }

    public static void RestoreOverflowPresentation(OverflowPresentation saved)
    {
        nint window=(nint)saved.Handle;
        if(ClassName(window) is "TopLevelWindowForOverflowXamlIsland" or "NotifyIconOverflowWindow")
            RestorePresentation(window,saved.Style);
    }

    static TaskbarPresentation ReadPresentation(nint window)
    {
        long style=(long)GetWindowLong(window,-20);uint key=0,flags=2;byte alpha=255;
        if((style&0x80000)!=0&&!GetLayeredWindowAttributes(window,out key,out alpha,out flags))
            throw new InvalidOperationException("Cannot preserve window opacity");
        return new(style,key,alpha,flags);
    }

    static bool MakeTransparent(nint window,TaskbarPresentation saved)
    {
        SetWindowLong(window,-20,(nint)(saved.Style|0x80000));
        return SetLayeredWindowAttributes(window,0,0,2);
    }

    public static TaskbarPresentation? RevealTaskbarTransparently()
    {
        nint bar=FindWindow("Shell_TrayWnd",null);if(bar==0||IsWindowVisible(bar))return null;
        var saved=ReadPresentation(bar);
        Program.SaveTaskbarPresentation(saved);
        if(!MakeTransparent(bar,saved)){RestoreTaskbarPresentation(saved);Program.SaveTaskbarPresentation(null);throw new InvalidOperationException("Cannot make taskbar transparent");}
        ShowWindow(bar,8);return saved;
    }
    public static void RestoreTaskbarPresentation(TaskbarPresentation saved)
    {
        nint bar=FindWindow("Shell_TrayWnd",null);if(bar==0)return;
        RestorePresentation(bar,saved);
    }
    static void RestorePresentation(nint window,TaskbarPresentation saved)
    {
        ShowWindow(window,0);
        if((saved.Style&0x80000)!=0)SetLayeredWindowAttributes(window,saved.Key,saved.Alpha,saved.Flags);
        SetWindowLong(window,-20,(nint)saved.Style);
    }

    public static bool IsTaskbarVisible => IsWindowVisible(FindWindow("Shell_TrayWnd", null));
    public static string ClassName(nint window) { var name = new StringBuilder(256); GetClassName(window, name, name.Capacity); return name.ToString(); }
    public static bool IsCloaked(nint window) => DwmGetWindowAttribute(window, 14, out int value, 4) == 0 && value != 0;
    public static string Title(nint window) { var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); return text.ToString(); }

    public static BitmapSource? Icon(string path)
    {
        if(ShellIcons.Load(path) is {} large)return large;
        var info = new FileInfo();
        try
        {
            if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<FileInfo>(), 0x100) == 0 || info.Icon == 0) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
        finally { if (info.Icon != 0) DestroyIcon(info.Icon); }
    }

    public static void Activate(nint window)
    {
        if (!IsWindow(window)) return;
        if (IsIconic(window)) ShowWindow(window, 9);
        SetForegroundWindow(window);
    }
    public static void PreviewWindow(nint window)
    {
        if(!IsWindow(window))return;
        if(IsIconic(window))ShowWindow(window,9);
        // Raising without activation also works while another app owns an OLE drag.
        SetWindowPos(window,0,0,0,0,0,0x13);
        SetForegroundWindow(window);
    }

    public static string Language()
    {
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        try { return System.Globalization.CultureInfo.GetCultureInfo((int)((long)GetKeyboardLayout(thread) & 0xFFFF)).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch { return "EN"; }
    }
}
