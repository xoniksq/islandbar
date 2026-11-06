using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IslandBar;

sealed partial class StorageWindow
{
    const string DropEffectFormat = "Preferred DropEffect";
    Border? _overlay;
    Action? _dismissConflict;
    int _previewRevision;
    StorageEntry[]? _dragItems;
    DropTarget? _dropTarget;
    StoragePlan[]? _droppedPlans;

    sealed record DropTarget(FrameworkElement Control, ListBoxItem? Row, string Folder, bool Move,
        StoragePlan[] Plans, string? Error);

    void ConnectDragSource(BrowserPane pane)
    {
        pane.Items.PreviewMouseLeftButtonDown += (_, e) => {
            pane.DragOrigin = e.GetPosition(pane.Items);
            var row = ItemRow(pane, e.OriginalSource);
            pane.DragCandidates = row?.Content is BrowserRow item
                ? row.IsSelected ? Selected(pane) : [item.Entry]
                : [];
        };
        pane.Items.PreviewMouseLeftButtonUp += (_, _) => pane.DragCandidates = [];
        pane.Items.PreviewMouseMove += (_, e) => {
            if (e.LeftButton != MouseButtonState.Pressed) { pane.DragCandidates = []; return; }
            if (_busy || _dragItems is not null || pane.DragCandidates.Length == 0 || e.StylusDevice is not null) return;
            var delta = e.GetPosition(pane.Items) - pane.DragOrigin;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var entries = pane.DragCandidates;
            pane.DragCandidates = [];
            if (!CheckBatchSize(entries.Length)) return;
            _dragItems = entries;
            _droppedPlans = null;
            string previousStatus = _status.Text;
            _dialog = true;
            try {
                var paths = StorageSelection.Normalize(entries).Select(entry => entry.Path).ToArray();
                var data = FileTransferData(paths, move: false);
                DragDrop.DoDragDrop(pane.Items, data, DragDropEffects.Copy);
            }
            finally {
                _dialog = false;
                _dragItems = null;
                string? error = _dropTarget?.Error;
                ClearDropTarget();
                Status(error ?? previousStatus);
            }
            if (_droppedPlans is { } plans) ShowPlan(plans);
            _droppedPlans = null;
        };
    }

    void ConnectDrop(BrowserPane pane, FrameworkElement control, bool tab)
    {
        control.AllowDrop = true;
        control.PreviewDragEnter += (_, e) => TrackDrop(pane, control, tab, e);
        control.PreviewDragOver += (_, e) => TrackDrop(pane, control, tab, e);
        control.DragLeave += (_, _) => { if (_dropTarget?.Control == control) ClearDropTarget(); };
        control.PreviewDrop += (_, e) => {
            TrackDrop(pane, control, tab, e);
            if (e.Effects == DragDropEffects.Copy) _droppedPlans = _dropTarget?.Plans;
        };
    }

    void TrackDrop(BrowserPane pane, FrameworkElement control, bool tab, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (_dragItems is not { } entries || _busy) return;
        var row = tab ? null : ItemRow(pane, e.OriginalSource);
        if (row?.Content is not BrowserRow { Entry.Folder: true }) row = null;
        string folder = (row?.Content as BrowserRow)?.Entry.Path ?? pane.Folder;
        bool move = !e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
        if (_dropTarget is not { } target || target.Control != control || target.Row != row ||
            target.Move != move || target.Folder != folder) {
            ClearDropTarget();
            var plans = StorageSelection.ToPlans(entries, folder, move);
            var reasons = plans.Select(plan => plan.Move ? plan.InvalidReason : plan.BlockReason).ToArray();
            string? blocked = reasons.All(reason => reason is not null) ? reasons.FirstOrDefault() : null;
            target = new(control, row, folder, move, plans, blocked);
            _dropTarget = target;
            if (blocked is null) {
                if (control is Border border) border.BorderBrush = PanelControls.Silver;
                if (row is not null) row.Tag = "drop-target";
                Status($"{(move ? "Перенести" : "Копировать")} · {plans.Length} → {folder}");
            } else Status(blocked);
        }
        // Only the storage queue performs internal moves. The Windows drop contract offers files for copy.
        if (target.Error is null && target.Plans.Length != 0)
            e.Effects = DragDropEffects.Copy;
    }

    void ClearDropTarget()
    {
        if (_dropTarget is not { } target) return;
        if (target.Row is { } row) row.ClearValue(TagProperty);
        var pane = _panes.FirstOrDefault(item => item.Frame == target.Control || item.Tab == target.Control);
        if (pane is not null) UpdatePaneAccent(pane);
        _dropTarget = null;
    }

    void OnShortcut(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; HandleEscape(); return; }
        if (_busy) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.F5 && modifiers == ModifierKeys.None ||
            key == Key.R && modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBoxBase) {
            e.Handled = true;
            Refresh();
            return;
        }
        if (key == Key.F1 && modifiers == ModifierKeys.None && _overlay is null) { e.Handled = true; ShowHelp(); return; }
        if (!ReferenceEquals(_page.Content, _files) || _overlay is not null) return;
        if (modifiers == ModifierKeys.Alt) {
            switch (key) {
                case Key.D: EditAddress(_active); break;
                case Key.P: PreviewSelection(_active); break;
                case Key.Up: GoUp(_active); break;
                case Key.Left: Step(_active, -1); break;
                case Key.Right: Step(_active, 1); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) {
            switch (key) {
                case Key.T: ReopenPane(); break;
                case Key.Tab: CyclePane(-1); break;
                case Key.N: ShowNewFolder(_active); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control) {
            switch (key) {
                case Key.T: AddPane(SelectedFolderOrCurrent(_active)); break;
                case Key.W: ClosePane(_active); break;
                case Key.Tab: CyclePane(1); break;
                case Key.L: EditAddress(_active); break;
                case Key.F: _search.Focus(); _search.SelectAll(); break;
                case Key.I: _active.Filter.Focus(); _active.Filter.SelectAll(); break;
                case Key.D: PinFolders([SelectedFolderOrCurrent(_active)]); break;
                case Key.Z when Keyboard.FocusedElement is not TextBoxBase:
                    if (!e.IsRepeat) _ = UndoLatest();
                    break;
                case Key.A when _active.Items.IsKeyboardFocusWithin: _active.Items.SelectAll(); break;
                case Key.C when Keyboard.FocusedElement is not TextBoxBase: CopyToClipboard(Selected(_active), false); break;
                case Key.X when Keyboard.FocusedElement is not TextBoxBase: CopyToClipboard(Selected(_active), true); break;
                case Key.V when Keyboard.FocusedElement is not TextBoxBase: Paste(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (modifiers != ModifierKeys.None || Keyboard.FocusedElement is TextBoxBase) return;
        switch (key) {
            case Key.F2 when Selected(_active) is [var entry]: ShowRename(_active, entry); break;
            case Key.F2: ShowBatchRename(_active, Selected(_active)); break;
            case Key.F3: PreviewSelection(_active); break;
            case Key.Delete when _active.Items.IsKeyboardFocusWithin:
                if (!e.IsRepeat) RecycleSelection(Selected(_active));
                break;
            case Key.Space when !_active.TouchSelection && Selected(_active) is [var preview] && CanPreview(preview):
                PreviewSelection(_active); break;
            case Key.Back when _active.Items.IsKeyboardFocusWithin: GoUp(_active); break;
            default: return;
        }
        e.Handled = true;
    }

    public void HandleEscape()
    {
        if (_openFolderMenu is { IsOpen: true } menu) { menu.IsOpen = false; return; }
        if (_dismissConflict is { } dismiss) { dismiss(); return; }
        if (_overlay is not null) { ClosePreview(); _active.Items.Focus(); return; }
        if (_busy) { Status("Перенос идёт. Можно остановиться после текущего элемента."); return; }
        if (_backFromDetail is { } back) { back(); return; }
        if (ReferenceEquals(_page.Content, _files)) {
            if (_active.EditingAddress) { CloseAddress(_active); _active.Items.Focus(); return; }
            if (_active.Filter.IsKeyboardFocusWithin && _active.Filter.Text.Length != 0) { _active.Filter.Clear(); return; }
            if (_search.IsKeyboardFocusWithin && _search.Text.Length != 0) { ClearSearch(); return; }
        }
        Dismiss();
    }

    void ChooseQuickFolders()
    {
        _dialog = true;
        try { Try(() => PinFolders(QuickFolders.Choose(this, _active.Folder))); }
        finally { _dialog = false; }
    }

    void PinFolders(string[] paths)
    {
        if (paths.Length == 0) return;
        Try(() => {
            var added = QuickFolders.AddMany(paths);
            if (added.Length == 0) { Status("Папки уже закреплены или недоступны."); return; }
            BuildSidebar(added[0]);
            Status(added.Length == 1 ? $"В быстрых папках · {PathCaption(added[0])}" : $"Добавлено папок: {added.Length}");
        });
    }

    void AddItemMenu(BrowserPane pane)
    {
        var menu = FolderMenu();
        pane.Items.ContextMenu = menu;
        pane.Items.PreviewMouseRightButtonDown += (_, e) => {
            SetActive(pane);
            if (ItemRow(pane, e.OriginalSource) is { } row) {
                if (!row.IsSelected) pane.Items.SelectedItem = row.Content;
            } else pane.Items.UnselectAll();
        };
        pane.Items.ContextMenuOpening += (_, e) => {
            menu.Items.Clear();
            var selected = Selected(pane);
            if (selected.Length == 0) {
                AddMenuItem(menu, "Вставить · Ctrl+V", Paste);
                AddMenuItem(menu, "Отменить действие · Ctrl+Z", () => _ = UndoLatest());
                AddMenuItem(menu, "Новая папка", () => ShowNewFolder(pane));
                AddMenuItem(menu, "Обновить · F5", Refresh);
                return;
            }
            if (selected is [var entry]) {
                AddMenuItem(menu, entry.Folder ? "Открыть папку" : "Открыть", () => OpenItem(pane, entry));
                if (entry.Folder) AddFolderActions(menu, entry.Path, pane);
                else {
                    AddMenuItem(menu, "Предпросмотр · F3", () => PreviewSelection(pane));
                    AddMenuItem(menu, "Открыть расположение", () => Navigate(pane, Path.GetDirectoryName(entry.Path)!, entry.Path));
                    AddMenuItem(menu, "Показать в Проводнике", () => Try(() => {
                        IslandWindow.ShowFolder(entry.Path, true); DismissOnOutside();
                    }));
                    AddMenuItem(menu, "Копировать путь", () => CopyPath(entry.Path));
                }
                AddMenuItem(menu, "Переименовать · F2", () => ShowRename(pane, entry));
                menu.Items.Add(new Separator());
            } else AddMenuItem(menu, "Копировать пути", () => CopyPath(string.Join(Environment.NewLine, selected.Select(item => item.Path))));
            AddMenuItem(menu, "Копировать · Ctrl+C", () => CopyToClipboard(selected, false));
            AddMenuItem(menu, "Вырезать · Ctrl+X", () => CopyToClipboard(selected, true));
            AddMenuItem(menu, "Вставить в эту панель · Ctrl+V", Paste);
            AddMenuItem(menu, "Отменить действие · Ctrl+Z", () => _ = UndoLatest());
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "В корзину · Delete", () => RecycleSelection(selected));
        };
    }

    void AddFolderActions(ContextMenu menu, string path, BrowserPane? source, bool removable = false)
    {
        if (_panes.Count < StorageNavigation.MaxPanes)
            AddMenuItem(menu, "Открыть в новой панели", () => AddPane(path));
        if (_panes.Count > 1)
            AddMenuItem(menu, "Открыть в следующей панели", () => {
                int index = _panes.IndexOf(source ?? _active);
                var next = _panes[(index + 1) % _panes.Count];
                FocusPane(next);
                Navigate(next, path);
            });
        if (source is not null)
            AddMenuItem(menu, "Добавить в быстрые папки", () => PinFolders([path]));
        AddMenuItem(menu, "Открыть в Проводнике", () => Try(() => { IslandWindow.ShowFolder(path); DismissOnOutside(); }));
        AddMenuItem(menu, "Копировать адрес", () => CopyPath(path));
        if (removable) AddMenuItem(menu, "Убрать из быстрых папок", () => Try(() => {
            QuickFolders.Remove(path);
            BuildSidebar();
            Status("Закрепление убрано.");
        }));
    }

    void CopyPath(string path) => Try(() => {
        Clipboard.SetText(path);
        Status("Путь скопирован.");
    });

    void CopyToClipboard(StorageEntry[] entries, bool move)
    {
        if (entries.Length == 0 || !CheckBatchSize(entries.Length)) return;
        Try(() => {
            string[] paths = StorageSelection.Normalize(entries).Select(entry => entry.Path).ToArray();
            var data = FileTransferData(paths, move);
            Clipboard.SetDataObject(data, copy: true);
            Status($"{(move ? "Вырезано" : "Скопировано в буфер")} · {paths.Length}. ПКМ → Вставить или Ctrl+V.");
        });
    }

    static DataObject FileTransferData(string[] paths, bool move)
    {
        var files = new StringCollection();
        files.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(files);
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(move ? 2 : 1)), autoConvert: false);
        return data;
    }

    static bool ClipboardRequestsMove(IDataObject data)
    {
        byte[]? bytes = data.GetData(DropEffectFormat, autoConvert: false) switch {
            MemoryStream stream => stream.ToArray(),
            byte[] buffer => buffer,
            _ => null
        };
        return bytes is { Length: >= 4 } && (BitConverter.ToInt32(bytes) & 3) == 2;
    }

    void Paste()
    {
        if (_busy) return;
        Try(() => PasteFiles(Clipboard.GetDataObject()));
    }

    void PasteFiles(IDataObject? data)
    {
        if (data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) {
            Status("В буфере нет файлов или папок.");
            return;
        }
        if (!CheckBatchSize(paths.Length)) return;
        bool move = ClipboardRequestsMove(data);
        var entries = paths.Where(Path.IsPathFullyQualified).Select(path => {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (Directory.Exists(full)) return StorageCatalog.Entry(new DirectoryInfo(full));
            if (File.Exists(full)) return StorageCatalog.Entry(new FileInfo(full));
            return new StorageEntry(Path.GetFileName(full), full, false, 0, DateTime.MinValue, 0);
        }).ToArray();
        var plans = StorageSelection.ToPlans(entries, _active.Folder, move);
        if (move) ShowPlan(plans);
        else _ = Execute(plans, 0, prepare: true, nameCopies: true);
    }

    bool CheckBatchSize(int count)
    {
        if (count <= StorageSelection.MaxBatch) return true;
        Status($"Выбери до {StorageSelection.MaxBatch} элементов за раз или перенеси папку целиком.");
        return false;
    }

    void ChooseTransfer(BrowserPane pane, bool move, Button anchor)
    {
        var entries = Selected(pane);
        if (entries.Length == 0 || !CheckBatchSize(entries.Length)) return;
        var menu = FolderMenu();
        for (int i = 0; i < _panes.Count; i++) {
            var other = _panes[i];
            if (other == pane || other.Folder.Equals(pane.Folder, StringComparison.OrdinalIgnoreCase)) continue;
            string path = other.Folder;
            string caption = path.Length > 72 ? "…" + path[^69..] : path;
            AddMenuItem(menu, $"Панель {i + 1} · {caption}", () => ShowPlan(StorageSelection.ToPlans(entries, path, move)), path);
        }
        AddMenuItem(menu, "Выбрать папку…", () => {
            if (PickFolder(pane.Folder) is { } folder) ShowPlan(StorageSelection.ToPlans(entries, folder, move));
        });
        OpenMenu(anchor, menu);
    }

    string? PickFolder(string folder)
    {
        _dialog = true;
        try {
            var dialog = new OpenFolderDialog { Title = "Выбрать папку назначения" };
            if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        }
        finally { _dialog = false; }
    }

    void ShowNewFolder(BrowserPane pane) => ShowNameEditor("Новая папка", pane.Folder, "", 0, "Создать", name => {
        if (_queue.Active is not null) throw new IOException("Создание папки доступно после завершения очереди.");
        string path = Path.Combine(pane.Folder, name);
        if (!Directory.Exists(pane.Folder)) throw new DirectoryNotFoundException("Исходная папка недоступна.");
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Такое имя уже занято.");
        Directory.CreateDirectory(path);
        _queue.ResetUndo("Последнее действие — создание папки.");
        Navigate(pane, path);
        Status("Папка создана.");
    });

    void ShowRename(BrowserPane pane, StorageEntry entry) => Try(() => {
        string identity = StorageTransfers.GetItemIdentity(entry.Path);
        int selection = entry.Folder ? entry.Name.Length : Path.GetFileNameWithoutExtension(entry.Name).Length;
        ShowNameEditor("Переименовать", entry.Path, entry.Name, selection, "Сохранить имя", name => {
            if (_queue.Active is not null) throw new IOException("Переименование доступно после завершения очереди.");
            string parent = Path.GetDirectoryName(entry.Path)!;
            string target = Path.Combine(parent, name);
            if (target == entry.Path) { ShowView("Файлы"); return; }
            if (StorageTransfers.GetItemIdentity(entry.Path) != identity)
                throw new IOException("Исходный элемент был заменён.");
            if (!target.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)))
                throw new IOException("Такое имя уже занято.");
            StorageTransfers.RenamePath(entry.Path, target);
            _queue.RememberRename([new(entry.Path, target, identity)]);
            if (entry.Folder) _navigation.Relocate(entry.Path, target);
            Navigate(pane, parent, target);
            Status("Имя изменено.");
        });
    });

    void ShowNameEditor(string title, string path, string value, int selectionLength, string button, Action<string> save)
    {
        ++_pageRevision;
        var rows = new StackPanel();
        Section(rows, title.ToUpperInvariant());
        rows.Children.Add(Text(path, 12, PanelControls.Secondary, wrap: true));
        var name = Field("Имя файла или папки");
        name.Text = value;
        var field = PanelControls.Well(name, new Thickness(10), 9);
        field.Margin = new Thickness(0, 8, 0, 8);
        rows.Children.Add(field);
        void SaveName() => Try(() => {
            string text = name.Text;
            if (string.IsNullOrWhiteSpace(text) || text.EndsWith('.') || text.EndsWith(' ') ||
                text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Введи обычное имя файла или папки.");
            save(text);
        });
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SaveName(); } };
        var actions = new WrapPanel();
        actions.Children.Add(Command("Назад", "Вернуться к файлам", () => ShowView("Файлы")));
        var apply = Command(button, button, SaveName);
        PanelControls.FinishTile(apply);
        actions.Children.Add(apply);
        rows.Children.Add(actions);
        ShowDetail(PanelControls.Well(PanelControls.Scroller(rows, double.PositiveInfinity), new Thickness(14)), () => ShowView("Файлы"));
        name.Focus();
        name.Select(0, selectionLength);
    }

    static bool CanPreview(StorageEntry entry) => !entry.Folder && !entry.OnlineOnly &&
        (entry.Kind == StorageKind.Image && entry.Bytes <= 40 * 1024 * 1024 || entry.Kind is StorageKind.Text or StorageKind.Video);

    void PreviewSelection(BrowserPane pane)
    {
        if (Selected(pane) is [var entry]) _ = ShowPreview(entry);
        else Status("Выбери один элемент для предпросмотра.");
    }

    async Task ShowPreview(StorageEntry entry)
    {
        if (entry.Kind == StorageKind.Video && !entry.OnlineOnly) { ShowVideo(entry); return; }
        string description = $"{(entry.Folder ? "Папка" : StorageCatalog.Size(entry.Bytes))} · {entry.Modified.ToLocalTime():dd.MM.yyyy HH:mm}\n{entry.Path}";
        var content = new ContentControl {
            Content = Text(CanPreview(entry) ? "Загружаю…" : "Сведения об элементе", 13, PanelControls.Secondary),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        ShowOverlay(entry.Name, description, content);
        if (!CanPreview(entry)) return;
        int revision = _previewRevision;
        try {
            if (entry.Kind == StorageKind.Image) {
                var bitmap = await Task.Run(() => {
                    using var file = File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var source = new BitmapImage();
                    source.BeginInit();
                    source.StreamSource = file;
                    source.CacheOption = BitmapCacheOption.OnLoad;
                    source.DecodePixelWidth = 960;
                    source.EndInit();
                    source.Freeze();
                    return source;
                });
                if (!_closed && revision == _previewRevision)
                    content.Content = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            } else {
                var text = await Task.Run(() => {
                    using var file = File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(file);
                    var buffer = new char[16000];
                    int count = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (Array.IndexOf(buffer, '\0', 0, count) >= 0) throw new FormatException("Файл содержит двоичные данные.");
                    return new string(buffer, 0, count) + (reader.EndOfStream ? "" : "\n\n… показано начало файла");
                });
                if (!_closed && revision == _previewRevision) {
                    var field = Field("Содержимое файла");
                    field.IsReadOnly = true;
                    field.Text = text;
                    field.FontFamily = new FontFamily("Consolas");
                    field.FontSize = 12;
                    field.TextWrapping = TextWrapping.Wrap;
                    field.VerticalAlignment = VerticalAlignment.Stretch;
                    field.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    content.Content = field;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FormatException or ArgumentException) {
            if (!_closed && revision == _previewRevision) content.Content = Text("Превью недоступно. " + ex.Message, 12, PanelControls.Secondary, wrap: true);
        }
    }

    void ShowOverlay(string title, string? description, UIElement content, Action? dismissConflict = null)
    {
        if (_busy && dismissConflict is null) return;
        ClosePreview();
        _dismissConflict = dismissConflict;
        var layout = new DockPanel();
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        AddDock(bar, Command("×", dismissConflict is null ? "Закрыть предпросмотр" : "Пропустить совпадение",
            dismissConflict ?? ClosePreview), Dock.Right);
        bar.Children.Add(Text(title, 17, weight: FontWeights.SemiBold));
        AddDock(layout, bar, Dock.Top);
        if (description is not null) {
            var info = Text(description, 11, PanelControls.Secondary, wrap: true);
            info.Margin = new Thickness(0, 0, 0, 10);
            AddDock(layout, info, Dock.Top);
        }
        layout.Children.Add(content);
        var card = PanelControls.Frame(layout, new Thickness(14));
        card.MaxWidth = dismissConflict is null ? 820 : 570;
        if (dismissConflict is not null) card.VerticalAlignment = VerticalAlignment.Center;
        card.Margin = new Thickness(10);
        var overlay = new Border { Background = new SolidColorBrush(Color.FromArgb(176, 0, 0, 0)), Child = card };
        if (dismissConflict is null)
            overlay.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == overlay) ClosePreview(); };
        _overlay = overlay;
        _pageLayer.Children.Add(overlay);
    }

    void ClosePreview()
    {
        _previewCleanup?.Invoke();
        _previewCleanup = null;
        _previewRevision++;
        _dismissConflict = null;
        if (_overlay is not { } overlay) return;
        _pageLayer.Children.Remove(overlay);
        _overlay = null;
    }

    void ShowQueueConflict(StoragePlan plan, StorageConflict conflict)
    {
        void Choose(QueueChoice action)
        {
            ClosePreview();
            _queue.Resolve(action);
        }

        var content = new StackPanel();
        content.Children.Add(ConflictItem("ПЕРЕНОСИМЫЙ ЭЛЕМЕНТ", plan.Source));
        content.Children.Add(ConflictItem("УЖЕ В ПАПКЕ НАЗНАЧЕНИЯ", conflict.Existing));
        if (conflict.Replacement is null)
            content.Children.Add(Text("Можно сохранить оба элемента под разными именами.", 12, PanelControls.Secondary, wrap: true));
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (conflict.Replacement is not null) {
            var replace = Command("Заменить", "Заменить файл в папке назначения", () => Choose(QueueChoice.Replace));
            PanelControls.FinishTile(replace);
            buttons.Children.Add(replace);
        }
        var skip = Command("Пропустить", "Пропустить этот элемент", () => Choose(QueueChoice.Skip));
        buttons.Children.Add(skip);
        buttons.Children.Add(Command("Сохранить оба", "Перенести под свободным именем", () => Choose(QueueChoice.KeepBoth)));
        buttons.Children.Add(Command("Остановить", "Остановить оставшийся перенос", () => Choose(QueueChoice.Stop)));
        content.Children.Add(buttons);
        ShowOverlay("Совпадение имён", "Выбери действие для этого элемента.", content, () => Choose(QueueChoice.Skip));
        skip.Focus();
    }

    static Border ConflictItem(string caption, StorageEntry entry)
    {
        var labels = new StackPanel();
        labels.Children.Add(PanelControls.Eyebrow(caption));
        labels.Children.Add(Text(entry.Name, 14, weight: FontWeights.SemiBold));
        var path = Text(entry.Path, 11, PanelControls.Secondary);
        path.ToolTip = PanelControls.Hint(entry.Path);
        labels.Children.Add(path);
        labels.Children.Add(Text($"{(entry.Folder ? "Папка" : StorageCatalog.Size(entry.Bytes))} · {entry.Modified.ToLocalTime():dd.MM.yyyy HH:mm}", 11, PanelControls.Secondary));
        var card = PanelControls.Well(labels, new Thickness(9, 6, 9, 6), 8);
        card.Margin = new Thickness(0, 0, 0, 5);
        return card;
    }

    static StackPanel TransferLabel(StoragePlan plan, string origin, bool preview = false)
    {
        var text = new StackPanel();
        text.Children.Add(Text(plan.Source.Name + "  ·  " +
            (plan.Source.Folder ? "папка с содержимым" : StorageCatalog.Size(plan.Source.Bytes)), 13));
        if (preview) {
            text.Children.Add(Text("Откуда  ·  " + origin, 12, PanelControls.Secondary, wrap: true));
            text.Children.Add(Text("Куда  ·  " + plan.Destination, 12, PanelControls.Silver, wrap: true));
        } else text.Children.Add(Text(origin + "   →   " + plan.Destination, 11, PanelControls.Secondary, wrap: true));
        return text;
    }

    async void ShowPlan(StoragePlan[] plans)
    {
        if (plans.Length == 0 || _busy || !CheckBatchSize(plans.Length)) return;
        if (plans[0].Move) {
            await Execute(plans, 0, prepare: true);
            return;
        }
        int revision = ++_pageRevision;
        string previous = _view;
        void Back() => ShowView(previous);
        var loading = new StackPanel();
        loading.Children.Add(Text("Проверяю имена и папку назначения…", 13, PanelControls.Secondary));
        loading.Children.Add(Command("Назад", "Вернуться без изменений", Back));
        ShowDetail(loading, Back);
        CheckedTransfer[] review;
        try { review = await Task.Run(() => StorageSelection.Check(plans)); }
        catch (Exception ex) {
            if (!_closed && revision == _pageRevision) Status(ex.Message);
            return;
        }
        if (_closed || revision != _pageRevision) return;
        var valid = review.Where(item => item.Error is null).Select(item => item.Plan).ToArray();
        int skipped = review.Length - valid.Length;
        var layout = new DockPanel();
        var rows = new StackPanel();
        Section(rows, "ПРОВЕРКА КОПИРОВАНИЯ");
        foreach (var item in review) {
            var label = TransferLabel(item.Plan, item.Plan.Source.Path, preview: true);
            if (item.Error is { } error) label.Children.Add(Text("Пропуск · " + error, 12, Brushes.Khaki, wrap: true));
            var card = PanelControls.Well(label, new Thickness(9, 6, 9, 6), 8);
            card.Margin = new Thickness(0, 0, 0, 4);
            rows.Children.Add(card);
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(Command("Назад", "Вернуться без изменений", Back));
        string caption = skipped == 0 ? "Подтвердить копирование"
            : $"Выполнить {valid.Length} из {review.Length}";
        var apply = Command(caption, caption, async () => await Execute(valid, skipped));
        apply.IsEnabled = valid.Length != 0;
        PanelControls.FinishTile(apply);
        actions.Children.Add(apply);
        AddDock(layout, actions, Dock.Bottom);
        layout.Children.Add(PanelControls.Scroller(rows, double.PositiveInfinity));
        ShowDetail(layout, Back);
        Status(skipped == 0 ? $"К копированию · {valid.Length}. Назначение проверено."
            : $"Доступно · {valid.Length}. Пропущено · {skipped}. Причины указаны в списке.");
    }

    Task Execute(StoragePlan[] plans, int skipped, bool prepare = false, bool nameCopies = false)
    {
        if (_busy || plans.Length == 0) return Task.CompletedTask;
        _inbox = null;
        ShowView("Файлы");
        _queue.Enqueue(plans, skipped, prepare, nameCopies);
        return Task.CompletedTask;
    }
}
