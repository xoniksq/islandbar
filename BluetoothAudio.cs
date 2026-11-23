using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace IslandBar;

static class BluetoothAudio
{
    static readonly PropertyKey Container = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    static readonly Guid BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    [StructLayout(LayoutKind.Sequential)]
    struct Property { public Guid Set; public uint Id, Flags; }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsControl {
        [PreserveSig] int KsProperty(ref Property property, uint size, nint data, uint length, out uint returned);
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class Enumerator {}
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDevices {
        void EnumAudioEndpoints();
        void GetDefaultAudioEndpoint();
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IEndpoint device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEndpoint {
        void Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    }

    public static bool Request(string container, bool connect, bool probe = false)
    {
        if (!Guid.TryParse(container, out var target)) return false;
        using var audio = new MMDeviceEnumerator();
        var devices = (IDevices)Activator.CreateInstance(Type.GetTypeFromCLSID(typeof(Enumerator).GUID, true)!)!;
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool success = false;
        try {
            foreach (var endpoint in audio.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active | DeviceState.Unplugged))
            using (endpoint) {
                try {
                    var properties = endpoint.Properties;
                    if (!properties.Contains(Container) || !Guid.TryParse(properties[Container].Value?.ToString(), out var id) || id != target) continue;
                    var filter = endpoint.DeviceTopology.GetConnector(0).ConnectedToDeviceId;
                    if (string.IsNullOrEmpty(filter) || !tried.Add(filter)) continue;
                    devices.GetDevice(filter, out var device);
                    try {
                        var iid = typeof(IKsControl).GUID;
                        device.Activate(ref iid, 23, 0, out var control);
                        try {
                            var property = new Property { Set = BtAudio, Id = connect ? 0u : 1u, Flags = probe ? 512u : 1u };
                            var buffer = probe ? Marshal.AllocHGlobal(4) : 0;
                            try {
                                var hr = ((IKsControl)control).KsProperty(ref property, (uint)Marshal.SizeOf<Property>(), buffer, probe ? 4u : 0u, out uint count);
                                success |= hr >= 0 && (!probe || count == 4 && (Marshal.ReadInt32(buffer) & 1) != 0);
                            } finally { if (buffer != 0) Marshal.FreeHGlobal(buffer); }
                        } finally { Marshal.ReleaseComObject(control); }
                    } finally { Marshal.ReleaseComObject(device); }
                } catch (InvalidCastException) { /* The filter does not expose IKsControl. */ } catch (COMException) { /* An endpoint can disappear while enumerating. */ }
            }
        } finally { Marshal.ReleaseComObject(devices); }
        return success;
    }
}
