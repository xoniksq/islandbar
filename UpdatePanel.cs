using System.Runtime.InteropServices;
using System.Net.Http;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace IslandBar;

sealed partial class IslandWindow
{
    [StructLayout(LayoutKind.Sequential)]
    struct LastInput { public uint Size, Tick; }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LastInput input);

    DateTime _nextUpdateCheck = DateTime.UtcNow.AddSeconds(12);
    DateTime _nextUpdateAttempt;
    string? _pendingUpdate;
    string _updateStatus = "Островок автоматически проверяет релизы на GitHub.";
    bool _checkingUpdate, _restartingForUpdate;
    Button? _updateButton;
    TextBlock? _updateHint;

    void AddUpdateSettings(Panel panel)
    {
        panel.Children.Add(SettingsHeading("ОБНОВЛЕНИЯ"));
        _updateButton = PanelControls.Button("Проверить обновления", "Проверить обновления");
        PanelControls.FinishTile(_updateButton);
        _updateButton.Click += async (_, _) => {
            if (_pendingUpdate is null) await CheckUpdates(interactive: true);
            else ApplyUpdate(automatic: false);
        };
        _updateHint = LeftLabel("", 11, Secondary);
        _updateHint.TextWrapping = TextWrapping.Wrap;
        _updateHint.Margin = new Thickness(3, 5, 3, 0);
        panel.Children.Add(_updateButton);
        var releases=PanelControls.Button("Открыть релизы GitHub","Открыть релизы GitHub");
        releases.Click+=(_,_)=>{
            try{Process.Start(new ProcessStartInfo("https://github.com/xoniksq/islandbar/releases/latest"){UseShellExecute=true})?.Dispose();}
            catch(Exception ex){Program.Log("open releases: "+ex.Message);PanelError("Не удалось открыть GitHub.");}
        };
        panel.Children.Add(releases);
        panel.Children.Add(_updateHint);
        UpdateUpdateControls();
    }

    void UpdateUpdateControls()
    {
        if (_updateButton is not null) {
            _updateButton.Content = _pendingUpdate is null ? "Проверить обновления" : "Перезапустить и обновить";
            _updateButton.IsEnabled = !_preview && !_checkingUpdate && !_restartingForUpdate;
        }
        if (_updateHint is not null) _updateHint.Text = _updateStatus;
    }

    void TickUpdates()
    {
        if (_preview || _closing || _restartingForUpdate) return;
        var now = DateTime.UtcNow;
        if (_pendingUpdate is null && !_checkingUpdate && now >= _nextUpdateCheck)
            _ = CheckUpdates(interactive: false);
        else if (_pendingUpdate is not null && now >= _nextUpdateAttempt && IdleForUpdate())
            ApplyUpdate(automatic: true);
    }

    static bool IdleForUpdate()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Tick) >= 60_000;
    }

    async Task CheckUpdates(bool interactive)
    {
        if (_checkingUpdate || _closing || _preview) return;
        _checkingUpdate = true;
        _nextUpdateCheck = DateTime.UtcNow.AddHours(4);
        _updateStatus = "Проверяю релизы GitHub…";
        UpdateUpdateControls();
        try {
            _pendingUpdate = await Task.Run(IslandUpdater.CheckAsync);
            _updateStatus = _pendingUpdate is null ? "Новой сборки нет." :
                $"Версия {_pendingUpdate} готова. Установится, когда островок свободен и ПК простаивает.";
        }
        catch (Exception ex) {
            _nextUpdateCheck = DateTime.UtcNow.AddMinutes(30);
            _updateStatus = ex switch {
                TimeoutException or OperationCanceledException => "GitHub или загрузка не ответили вовремя. Проверю позже.",
                HttpRequestException { StatusCode: { } code } => $"GitHub ответил ошибкой {(int)code}. Проверю позже.",
                HttpRequestException => "Не удалось подключиться к GitHub. Проверю позже.",
                UnauthorizedAccessException => "Нет доступа на запись в папку островка.",
                IOException => "Не удалось сохранить или проверить сборку. Подробности в log.txt.",
                _ => "Не удалось проверить обновления. Подробности в log.txt."
            };
            Program.Log("update check: " + ex.Message);
            if (interactive && _featureKind == "settings") PanelError(_updateStatus);
        }
        finally {
            _checkingUpdate = false;
            if (!_closing) { UpdateUpdateControls(); WriteStatus(); }
        }
    }

    void ApplyUpdate(bool automatic)
    {
        if (_pendingUpdate is null || _restartingForUpdate || _closing) return;
        if (_fullscreen || _storageWindow is not null || _storageQueue?.Active is not null || _setEditor is not null ||
            _fileDragging || _iconReordering || _searchWindow is not null || _startMenu is not null || _openMenus.Count > 0 ||
            automatic && (_featurePopup.IsOpen || _windowPreview is not null)) {
            if (!automatic) PanelError("Закрой хранилище и дождись завершения переносов.");
            return;
        }
        _nextUpdateAttempt = DateTime.UtcNow.AddMinutes(5);
        try {
            if (!IslandUpdater.StartHelper()) {
                _pendingUpdate = null;
                _nextUpdateCheck = DateTime.UtcNow.AddMinutes(5);
                _updateStatus = "Подготовленная сборка недоступна. Проверю ещё раз.";
                UpdateUpdateControls();
                return;
            }
            _restartingForUpdate = true;
            Close();
        }
        catch (Exception ex) {
            _restartingForUpdate = false;
            Program.Log("update start: " + ex.Message);
            if (!automatic) PanelError("Не удалось запустить обновление.");
        }
    }
}
