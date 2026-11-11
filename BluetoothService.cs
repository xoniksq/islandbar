using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace IslandBar;

sealed record BluetoothAccessory(string Name, bool Connected);

static class BluetoothService
{
    const string Connected = "System.Devices.Aep.IsConnected";
    const string ContainerId = "System.Devices.Aep.ContainerId";
    const string Address = "System.Devices.Aep.DeviceAddress";

    public static async Task<Radio?> GetRadioAsync(CancellationToken token)
    {
        var radios = await Radio.GetRadiosAsync().AsTask(token);
        return radios.Where(radio => radio.Kind == RadioKind.Bluetooth)
            .OrderByDescending(radio => radio.State == RadioState.On)
            .ThenByDescending(radio => radio.State == RadioState.Off)
            .FirstOrDefault();
    }

    public static async Task<BluetoothAccessory[]> GetPairedAsync(CancellationToken token)
    {
        string[] properties = [Connected, ContainerId, Address];
        var classic = DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            properties, DeviceInformationKind.AssociationEndpoint).AsTask(token);
        var lowEnergy = DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            properties, DeviceInformationKind.AssociationEndpoint).AsTask(token);
        var devices = await Task.WhenAll(classic, lowEnergy);
        return devices.SelectMany(group => group)
            .GroupBy(DeviceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new BluetoothAccessory(
                group.Select(device => device.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Устройство Bluetooth",
                group.Any(device => device.Properties.TryGetValue(Connected, out var connected) && connected is true)))
            .OrderByDescending(device => device.Connected)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    static string DeviceKey(DeviceInformation device)
    {
        if (device.Properties.TryGetValue(ContainerId, out var container) &&
            Guid.TryParse(container?.ToString(), out var id) && id != Guid.Empty) return id.ToString();
        if (device.Properties.TryGetValue(Address, out var address) && address is string { Length: > 0 } mac) return mac;
        return device.Id;
    }

    public static async Task<RadioState> SetStateAsync(Radio radio, RadioState state, CancellationToken token)
    {
        var access = await Radio.RequestAccessAsync().AsTask(token);
        if (access != RadioAccessStatus.Allowed || await radio.SetStateAsync(state).AsTask(token) != RadioAccessStatus.Allowed)
            throw new UnauthorizedAccessException("Windows ограничивает управление адаптером Bluetooth.");

        for (int attempt = 0; radio.State != state && attempt < 12; attempt++)
            await Task.Delay(150, token);
        return radio.State;
    }

    public static async Task<DevicePairingResultStatus?> PairFromPickerAsync(nint owner, Windows.Foundation.Rect anchor, CancellationToken token)
    {
        var picker = new DevicePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
        picker.Filter.SupportedDeviceSelectors.Add(BluetoothDevice.GetDeviceSelectorFromPairingState(false));
        picker.Filter.SupportedDeviceSelectors.Add(BluetoothLEDevice.GetDeviceSelectorFromPairingState(false));
        try {
            var device = await picker.PickSingleDeviceAsync(anchor, Windows.UI.Popups.Placement.Above).AsTask(token);
            if (device is null) return null;
            return (await device.Pairing.PairAsync().AsTask(token)).Status;
        }
        finally { picker.Hide(); }
    }
}
