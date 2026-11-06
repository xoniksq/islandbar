using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IslandBar;

static class DesktopIcons
{
    const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public static bool Hidden => Registry.GetValue(@"HKEY_CURRENT_USER\" + Explorer, "HideIcons", 0) is 1;

    public static bool SetHidden(bool hidden)
    {
        if (Hidden == hidden) return true;
        nint view = FindWindowEx(Native.FindWindow("Progman", null), 0, "SHELLDLL_DefView", null);
        if (view == 0) Native.EnumWindows((window, _) => {
            view = FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            return view == 0;
        }, 0);
        if (view == 0 || SendMessageTimeout(view, 0x0111, 0x7402, 0, 2, 1000, out _) == 0) return false;
        return Hidden == hidden;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint SendMessageTimeout(nint window, uint message, nint wparam, nint lparam,
        uint flags, uint milliseconds, out nint result);
}
