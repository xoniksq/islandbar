using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32.SafeHandles;

namespace IslandBar;

sealed partial class SearchWindow
{
    enum PowerAction { Lock, Sleep, ShutDown, Restart }
    public bool ConfirmingPower { get; private set; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);

    [StructLayout(LayoutKind.Sequential)]
    struct TokenPrivilege { public uint Count, LowId; public int HighId; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValue(string? system, string name, out long id);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivilege privileges,
        uint previousSize, out TokenPrivilege previous, out uint size);

    static void Sleep()
    {
        const uint queryAndAdjust = 0x0008 | 0x0020;
        if (!OpenProcessToken(new nint(-1), queryAndAdjust, out var token)) throw new Win32Exception();
        using (token) {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out long id)) throw new Win32Exception();
            var privilege = new TokenPrivilege { Count = 1, LowId = (uint)id, HighId = (int)(id >> 32), Attributes = 2 };
            uint size = (uint)Marshal.SizeOf<TokenPrivilege>();
            bool adjusted = AdjustTokenPrivileges(token, false, ref privilege, size, out var previous, out _);
            int error = Marshal.GetLastWin32Error();
            if (!adjusted || error != 0) throw new Win32Exception(error);
            try {
                if (!SetSuspendState(false, false, false)) throw new Win32Exception();
            }
            finally { AdjustTokenPrivileges(token, false, ref previous, size, out _, out _); }
        }
    }

    Button BuildPowerButton()
    {
        var glyph = new TextBlock { Text = "\uE7E8", FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"),
            FontSize = 17, Foreground = PanelControls.Silver };
        var button = PanelControls.Button(glyph, "Питание", 31, true);
        button.Width = 36;
        button.ToolTip = PanelControls.Hint("Питание");
        var menu = IslandWindow.CreateMenu();
        _start?.TrackContextMenu(menu);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Top;
        foreach (var (action, text) in new[] {
            (PowerAction.Lock, "Блокировка"), (PowerAction.Sleep, "Спящий режим"),
            (PowerAction.ShutDown, "Завершение работы"), (PowerAction.Restart, "Перезагрузка")
        }) {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => RunPower(action, text);
            menu.Items.Add(item);
        }
        button.ContextMenu = menu;
        button.Click += (_, _) => menu.IsOpen = !menu.IsOpen;
        return button;
    }

    void RunPower(PowerAction action, string title)
    {
        try {
            if (action is PowerAction.ShutDown or PowerAction.Restart) {
                ConfirmingPower = true;
                try {
                    if (MessageBox.Show(this, action == PowerAction.ShutDown ? "Завершить работу компьютера?" : "Перезагрузить компьютер?",
                        title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                } finally { ConfirmingPower = false; }
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe")) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(action == PowerAction.ShutDown ? "/s" : "/r");
                start.ArgumentList.Add("/t");
                start.ArgumentList.Add("0");
                using var process = Process.Start(start);
                if (process is null) throw new InvalidOperationException("Не удалось запустить команду Windows.");
            } else if (action == PowerAction.Sleep) Sleep();
            else if (!LockWorkStation()) throw new Win32Exception();
            Dismiss();
        }
        catch (Exception ex) {
            ShowLaunchError("Windows не выполнила действие.");
            Program.Log("start power: " + ex.Message);
        }
    }
}
