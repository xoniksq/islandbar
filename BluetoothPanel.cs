using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace IslandBar;

sealed partial class IslandWindow
{
    const string BluetoothGlyph = "\uE702";
    bool _bluetoothOperation, _bluetoothDialogOpen;

    void OpenBluetooth()
    {
        var column = CardColumn(350);
        column.Children.Add(CardTitle("Bluetooth"));
        var state = LeftLabel("Проверяю адаптер…", 12);
        var toggle = new CheckBox {
            Content = state, Template = SettingsSwitch, Height = 40, IsEnabled = false, Cursor = Cursors.Hand
        };
        AutomationProperties.SetName(toggle, "Bluetooth");
        column.Children.Add(PanelControls.Well(toggle, new Thickness(3)));

        var heading = PanelControls.Eyebrow("СОПРЯЖЁННЫЕ УСТРОЙСТВА");
        heading.Margin = new Thickness(0, 16, 0, 6);
        column.Children.Add(heading);
        var list = new StackPanel();
        var empty = LeftLabel("", 12, Secondary);
        empty.TextWrapping = TextWrapping.Wrap;
        empty.Margin = new Thickness(0, 3, 0, 6);
        column.Children.Add(empty);
        column.Children.Add(PanelControls.Scroller(list, 210));

        var add = PanelControls.Button("+  Добавить устройство", "Добавить устройство Bluetooth");
        PanelControls.FinishTile(add);
        add.IsEnabled = false;
        add.Margin = new Thickness(0, 12, 0, 4);
        column.Children.Add(add);
        var settings = PanelControls.Button("Настройки Bluetooth в Windows  ↗", "Настройки Bluetooth в Windows", 34);
        settings.Click += (_, _) => {
            try {
                Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true })?.Dispose();
                CloseFeature();
            }
            catch (Exception ex) {
                PanelError("Не удалось открыть настройки Bluetooth.");
                Program.Log("bluetooth settings: " + ex.Message);
            }
        };
        column.Children.Add(settings);

        Radio? radio = null;
        RadioState? observedState = null;
        BluetoothAccessory[] shown = [];
        bool reading = false;
        CancellationTokenSource? lifetime = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        void ShowRadio(RadioState? value)
        {
            toggle.IsChecked = value == RadioState.On;
            toggle.IsEnabled = !_bluetoothOperation && value is RadioState.On or RadioState.Off;
            add.IsEnabled = !_bluetoothOperation && value == RadioState.On;
            state.Text = value switch {
                RadioState.On => "Включён",
                RadioState.Off => "Выключен",
                RadioState.Disabled => "Адаптер отключён",
                RadioState.Unknown => "Состояние недоступно",
                _ => "Адаптер не найден"
            };
        }

        void ShowDevices(BluetoothAccessory[] devices, string message)
        {
            empty.Text = message;
            empty.Visibility = devices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (shown.SequenceEqual(devices)) return;
            shown = devices;
            list.Children.Clear();
            foreach (var device in devices) {
                var row = new DockPanel();
                var icon = Glyph(BluetoothGlyph, 15);
                icon.Width = 25;
                icon.Foreground = device.Connected ? Accent : Secondary;
                icon.Margin = new Thickness(0, 0, 7, 0);
                row.Children.Add(icon);
                var text = new StackPanel();
                var name = LeftLabel(device.Name, 12);
                name.ToolTip = PanelControls.Hint(device.Name);
                text.Children.Add(name);
                text.Children.Add(LeftLabel(device.Connected ? "Подключено" : "Сопряжено", 11, Secondary));
                row.Children.Add(text);
                var tile = PanelControls.Well(row, new Thickness(8, 6, 8, 6), 10);
                tile.Margin = new Thickness(0, 0, 0, 4);
                list.Children.Add(tile);
            }
        }

        async Task Refresh()
        {
            if (lifetime is not { } session || reading || _bluetoothOperation || _featureKind != "bluetooth") return;
            reading = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try {
                radio = await BluetoothService.GetRadioAsync(timeout.Token);
                if (session.IsCancellationRequested || _featureKind != "bluetooth" || _bluetoothOperation) return;
                observedState = radio?.State;
                ShowRadio(observedState);
                if (observedState != RadioState.On) {
                    ShowDevices([], observedState == RadioState.Off
                        ? "Для просмотра устройств включи Bluetooth." : "Список устройств недоступен.");
                    return;
                }
                var devices = await BluetoothService.GetPairedAsync(timeout.Token);
                if (!session.IsCancellationRequested && _featureKind == "bluetooth" && !_bluetoothOperation)
                    ShowDevices(devices, "Сопряжённых устройств пока нет.");
            }
            catch (Exception ex) {
                if (session.IsCancellationRequested || _featureKind != "bluetooth") return;
                if (radio is null) {
                    observedState = RadioState.Unknown;
                    ShowRadio(observedState);
                }
                ShowDevices([], "Не удалось прочитать список устройств.");
                Program.Log("bluetooth read: " + ex.Message);
            }
            finally { reading = false; }
        }

        toggle.Click += async (_, _) => {
            if (_bluetoothOperation || radio is not { } adapter || observedState is not (RadioState.On or RadioState.Off)) return;
            RadioState target = toggle.IsChecked == true ? RadioState.On : RadioState.Off;
            _bluetoothOperation = true;
            if (_panelError is not null) _panelError.Visibility = Visibility.Collapsed;
            ShowRadio(observedState);
            state.Text = target == RadioState.On ? "Включаю…" : "Выключаю…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try {
                observedState = await BluetoothService.SetStateAsync(adapter, target, timeout.Token);
                if (column.IsLoaded && _featureKind == "bluetooth" && observedState != target)
                    PanelError("Адаптер не изменил состояние. Проверь настройки Windows.");
            }
            catch (Exception ex) {
                if (column.IsLoaded && _featureKind == "bluetooth")
                    PanelError("Не удалось переключить Bluetooth. Попробуй через настройки Windows.");
                Program.Log("bluetooth toggle: " + ex.Message);
            }
            finally {
                _bluetoothOperation = false;
                ShowRadio(observedState);
                await Refresh();
            }
        };

        add.Click += async (_, _) => {
            if (_bluetoothOperation || lifetime is not { } session ||
                PresentationSource.FromVisual(add) is not HwndSource { RootVisual: UIElement root } source) return;
            var point = add.TranslatePoint(new Point(), root);
            var anchor = new Windows.Foundation.Rect(point.X, point.Y, add.ActualWidth, add.ActualHeight);
            _bluetoothOperation = _bluetoothDialogOpen = true;
            if (_panelError is not null) _panelError.Visibility = Visibility.Collapsed;
            ShowRadio(observedState);
            SyncOutsideTracking();
            try {
                var result = await BluetoothService.PairFromPickerAsync(source.Handle, anchor, session.Token);
                if (result is not (null or DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired) &&
                    column.IsLoaded && _featureKind == "bluetooth")
                    PanelError("Не удалось добавить устройство. Попробуй через настройки Windows.");
            }
            catch (Exception ex) {
                if (!session.IsCancellationRequested && column.IsLoaded && _featureKind == "bluetooth")
                    PanelError("Не удалось открыть выбор устройств. Используй настройки Bluetooth в Windows.");
                if (!session.IsCancellationRequested) Program.Log("bluetooth pairing: " + ex.Message);
            }
            finally {
                _bluetoothOperation = _bluetoothDialogOpen = false;
                SyncOutsideTracking();
                ShowRadio(observedState);
                await Refresh();
            }
        };

        timer.Tick += async (_, _) => await Refresh();
        column.Loaded += async (_, _) => {
            lifetime = new CancellationTokenSource();
            timer.Start();
            await Refresh();
        };
        column.Unloaded += (_, _) => {
            timer.Stop();
            lifetime?.Cancel();
            lifetime?.Dispose();
            lifetime = null;
        };
        OpenCard("bluetooth", column);
    }
}
