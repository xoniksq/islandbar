using System.Windows;
using System.Windows.Controls;

namespace IslandBar;

sealed partial class IslandWindow
{
    static string StorageLayoutPath => Path.Combine(Program.Folder, "storage-layout.json");
    StorageNavigation? _storageNavigation;
    readonly StorageTransfers _storageTransfers = new(Path.Combine(Program.Folder, "storage-journal.json"));
    StorageQueue? _storageQueue;
    Button? _queueButton;
    string QueueIndicator => _storageQueue?.Conflict is not null ? "!" : _storageQueue?.Active is { } job
        ? $"{100 * job.Finished / job.Plans.Length}%" : "";

    void InitializeQueueButton()
    {
        _queueButton = MakeButton(Label("⇄", 12), "Очередь переносов", 42);
        _queueButton.Click += (_, _) => OpenTransferQueue();
        _dock.Children.Insert(_dock.Children.IndexOf(_scroll), _queueButton);
    }

    void UpdateQueueIndicator()
    {
        var mark = QueueIndicator;
        if (_queueButton?.Content is TextBlock label) label.Text = mark.Length == 0 ? "⇄" : mark;
        _smallClock.Text = _clock.Text + (mark.Length == 0 ? "" : "  " + mark);
    }
    StorageQueue TransferQueue
    {
        get {
            if (_storageQueue is not null) return _storageQueue;
            var queue = new StorageQueue(_storageTransfers);
            queue.Transferred += result => {
                if (!result.Move || !result.IsFolder || _storageNavigation is not { } navigation) return;
                navigation.Relocate(result.Source, result.Destination);
                navigation.Save(StorageLayoutPath);
            };
            queue.Changed += UpdateQueueIndicator;
            return _storageQueue = queue;
        }
    }

    void OpenTransferQueue()
    {
        EnsureStorage();
        _storageWindow?.OpenQueue();
        _storageWindow?.Activate();
    }

    void EnsureStorage()
    {
        if (_storageWindow is null) OpenStorage();
        else _storageWindow.Activate();
    }

    internal void OpenStorage()
    {
        if (_closing || UpdateFullscreenVisibility()) return;
        if (_storageWindow is { } existing) {
            if (existing.KeepOpen && !existing.IsActive) existing.Activate();
            else existing.Dismiss();
            return;
        }
        CloseStartMenu(immediate: true);
        CloseFeature(immediate: true);
        ClosePreview();
        CloseMenus();
        _searchWindow?.Dismiss(immediate: true);
        if (_featureTriggers.TryGetValue("storage", out var trigger) &&
            trigger.ToolTip is System.Windows.Controls.ToolTip tip) tip.IsOpen = false;
        var navigation = _storageNavigation ??= StorageNavigation.Load(StorageLayoutPath);
        var area = DockDisplayArea();
        double width = Math.Min(navigation.Width, Width - 12);
        double bottom = Top + Height - _frame.Margin.Bottom - TargetHeight - 10;
        double height = Math.Min(navigation.Height, Math.Max(360, bottom - area.Top - 12));
        var window = new StorageWindow(new Rect(Left + (Width - width) / 2, Math.Max(area.Top + 12, bottom - height), width, height),
            navigation, _storageTransfers, StorageLayoutPath, TransferQueue) {
            CaptureProjectWindows = CaptureWorkspaceWindows,
            RestoreProjectWindows = RestoreWorkspaceWindows
        };
        _storageWindow = window;
        window.Closed += (_, _) => {
            _storageWindow = null;
            UpdatePanelMarker();
            SyncOutsideTracking();
            WriteStatus();
        };
        UpdatePanelMarker();
        window.Show();
        SyncOutsideTracking();
        WriteStatus();
    }
}
