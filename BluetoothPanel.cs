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

        string? selected = null;
        bool confirm = false;
        async Task Act(BluetoothAccessory device, bool remove)
        {
            if (_bluetoothOperation || lifetime is not {} session) return;
            _bluetoothOperation = true;
            PanelError(remove ? "Удаляю сопряжение…" : device.Connected ? "Отключаю…" : "Подключаю…");
            list.IsEnabled = false;
            ShowRadio(observedState);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try {
                if (remove) {
                    var info = await DeviceInformation.CreateFromIdAsync(device.DeviceId, [], DeviceInformationKind.AssociationEndpoint).AsTask(timeout.Token);
                    var result = await info.Pairing.UnpairAsync().AsTask(timeout.Token);
                    if (result.Status is not (DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired))
                        throw new InvalidOperationException("Windows не удалось удалить сопряжение.");
                } else {
                    if (!await Task.Run(() => BluetoothAudio.Request(device.Key, !device.Connected)))
                        throw new InvalidOperationException("Драйвер не принял команду. Открой настройки Bluetooth в Windows.");
                    while (true) {
                        var devices = await BluetoothService.GetPairedAsync(timeout.Token);
                        if (devices.Any(d => d.Key == device.Key && d.Connected != device.Connected)) break;
                        await Task.Delay(500, timeout.Token);
                    }
                }
                if (!session.IsCancellationRequested) PanelError(remove ? "Сопряжение удалено." : device.Connected ? "Устройство отключено." : "Устройство подключено.");
            } catch (Exception ex) {
                if (!session.IsCancellationRequested) PanelError(ex is OperationCanceledException ? "Устройство не ответило. Проверь питание и расстояние." : ex.Message);
            } finally {
                _bluetoothOperation = false;
                confirm = false;
                list.IsEnabled = true;
                if (!session.IsCancellationRequested) { ShowRadio(observedState); ShowDevices(shown, "", true); await Refresh(); }
            }
        }

        void ShowDevices(BluetoothAccessory[] devices, string message, bool force = false)
        {
            empty.Text = message;
            empty.Visibility = devices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!force && shown.SequenceEqual(devices)) return;
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
                var arrow = LeftLabel(selected == device.Key ? "⌃" : "⌄", 14, Secondary);
                DockPanel.SetDock(arrow, Dock.Right);
                row.Children.Add(arrow);
                row.Children.Add(text);
                var tile = PanelControls.Button(row, device.Name + " · Действия", 44);
                tile.Click += (_, _) => {
                    selected = selected == device.Key ? null : device.Key;
                    confirm = false;
                    ShowDevices(shown, message, true);
                };
                tile.Margin = new Thickness(0, 0, 0, 4);
                list.Children.Add(tile);
                if (selected != device.Key) continue;
                string action = device.Connected ? "Отключить" : "Подключить";
                var connection = PanelControls.Button(action, action + " " + device.Name, 32);
                connection.Click += async (_, _) => await Act(device, false);
                list.Children.Add(connection);
                string label = confirm ? "Подтвердить удаление сопряжения" : "Удалить сопряжение…";
                var forget = PanelControls.Button(label, label, 32);
                forget.Click += async (_, _) => {
                    if (confirm) { await Act(device, true); return; }
                    confirm = true;
                    ShowDevices(shown, message, true);
                };
                list.Children.Add(forget);
                if (confirm) {
                    var cancel = PanelControls.Button("Отмена", "Отменить удаление", 30);
                    cancel.Click += (_, _) => { confirm = false; ShowDevices(shown, message, true); };
                    list.Children.Add(cancel);
                }
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
