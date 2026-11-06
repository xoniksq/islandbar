using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace IslandBar;

sealed partial class StorageWindow
{
    readonly List<BrowserPane> _panes = [];
    readonly DockPanel _files = new();
    readonly Grid _paneGrid = new();
    readonly WrapPanel _paneTabs = new() { Margin = new Thickness(0, 0, 0, 7) };
    readonly TextBox _search = Field("Поиск по имени файла или папки");
    readonly TextBlock _selection = Text("", 11, PanelControls.Secondary);
    readonly string _downloadsFolder = Path.TrimEndingDirectorySeparator(QuickFolders.Downloads);
    BrowserPane _active = null!;
    StorageLayout? _arrangement;
    Button _newPane = null!, _focusMode = null!, _placesButton = null!, _searchScope = null!;
    Button _selectMode = null!, _copy = null!, _move = null!, _preview = null!;

    sealed class BrowserPane(StoragePaneState state, string downloadsFolder)
    {
        public StoragePaneState State { get; } = state;
        public FolderHistory History => State.History;
        public string Folder => History.Current;
        bool UsesDownloadsOrder => Query.Length == 0 &&
            Path.TrimEndingDirectorySeparator(Folder).Equals(downloadsFolder, StringComparison.OrdinalIgnoreCase);
        public StorageOrder Order {
            get => UsesDownloadsOrder ? State.DownloadsOrder : State.Order;
            set {
                if (UsesDownloadsOrder) State.DownloadsOrder = value;
                else State.Order = value;
            }
        }
        public TextBox Address { get; } = Field("Адрес папки");
        public TextBox Filter { get; } = Field("Быстрый фильтр списка");
        public ListBox Items { get; } = new() {
            SelectionMode = SelectionMode.Extended, Background = Brushes.Transparent, Foreground = Brushes.White,
            BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        public TextBlock Detail { get; } = Text("", 11, PanelControls.Secondary);
        public Button Back { get; set; } = null!;
        public Button Forward { get; set; } = null!;
        public Button SortButton { get; set; } = null!;
        public Border Frame { get; set; } = null!;
        public Border Tab { get; set; } = null!;
        public TextBlock TabTitle { get; set; } = null!;
        public Button LocationButton { get; set; } = null!;
        public TextBlock LocationTitle { get; set; } = null!;
        public DockPanel Breadcrumb { get; set; } = null!;
        public Button ParentLocation { get; set; } = null!;
        public TextBlock ParentTitle { get; set; } = null!;
        public StorageListing? Listing;
        public CancellationTokenSource? Read;
        public string? SelectPath;
        public string Query = "";
        public bool SearchGlobal, Dirty = true, Rendering, TouchSelection, EditingAddress;
        public Point DragOrigin;
        public StorageEntry[] DragCandidates = [];
    }

    sealed record BrowserRow(StorageEntry Entry, string Detail, string? Parent, string? DateGroup)
    {
        public string Name => Entry.Name;
        public string Path => Entry.Path;
        public string Icon => Entry.OnlineOnly ? "\uE753" : Entry.Folder ? "\uE8B7" : Entry.Kind switch {
            StorageKind.Image => "\uEB9F",
            StorageKind.Video => "\uE714",
            StorageKind.Audio => "\uE8D6",
            StorageKind.Archive => "\uF012",
            _ => "\uE8A5"
        };
    }

    void BuildBrowser()
    {
        var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        AddDock(searchRow, Command("×", "Очистить поиск", ClearSearch), Dock.Right);
        AddDock(searchRow, Command("Найти", "Искать по имени · Enter", Search), Dock.Right);
        _searchScope = Command("В папке", "Переключить поиск между текущей папкой и всеми дисками", () => {
            _active.SearchGlobal = !_active.SearchGlobal;
            _searchScope.Content = _active.SearchGlobal ? "Везде" : "В папке";
            if (_active.Query.Length != 0) Search();
        });
        AddDock(searchRow, _searchScope, Dock.Right);
        var searchField = PanelControls.Well(Placeholder(_search, "Поиск файлов и папок · Ctrl+F"), new Thickness(10, 0, 10, 0), 9);
        searchField.MinHeight = 32;
        searchRow.Children.Add(searchField);
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Search(); } };
        AddDock(_files, searchRow, Dock.Top);

        _placesButton = Command("Места", "Показать или скрыть быстрые места", TogglePlaces, 29);
        _newPane = Command("+ Панель", "Добавить панель · Ctrl+T", () => AddPane(SelectedFolderOrCurrent(_active)), 29);
        PanelControls.FinishTile(_newPane);
        _focusMode = Command("Фокус", "Переключить режим одной видимой панели", () => {
            _navigation.FocusMode = !_navigation.FocusMode;
            ArrangePanes();
            ScheduleSave();
        }, 29);
        AddDock(_files, _paneTabs, Dock.Top);

        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _selectMode = Command("Выбор", "Переключить выбор нескольких элементов одним нажатием", ToggleSelectionMode, 29);
        _copy = Command("Копировать…", "Копировать выбранное в другую панель или папку", () => ChooseTransfer(_active, false, _copy), 29);
        _move = Command("Перенести…", "Перенести выбранное в другую панель или папку", () => ChooseTransfer(_active, true, _move), 29);
        _preview = Command("Превью", "Предпросмотр выбранного · F3", () => PreviewSelection(_active), 29);
        actions.Children.Add(_selectMode);
        actions.Children.Add(_copy);
        actions.Children.Add(_move);
        actions.Children.Add(Command("Вставить", "Вставить файлы из буфера · Ctrl+V", Paste, 29));
        actions.Children.Add(_preview);
        actions.Children.Add(_selection);
        _selection.Margin = new Thickness(9, 3, 0, 3);
        AddDock(_files, actions, Dock.Bottom);

        var body = new DockPanel();
        BuildPlaces(body);
        body.Children.Add(_paneGrid);
        _files.Children.Add(body);
        _paneGrid.SizeChanged += (_, _) => ArrangePanes();

        foreach (var state in _navigation.Panes) AttachPane(state);
        _active = _panes[_navigation.ActiveIndex];
        BuildPaneTabs();
    }

    void AttachPane(StoragePaneState state)
    {
        var pane = new BrowserPane(state, _downloadsFolder);
        pane.Frame = BuildPane(pane);
        pane.Frame.Visibility = Visibility.Collapsed;
        _panes.Insert(_navigation.Panes.IndexOf(state), pane);
        _paneGrid.Children.Add(pane.Frame);
        _arrangement = null;
    }

    void AddPane(string path)
    {
        if (_busy) return;
        if (_navigation.Add(path) is not { } state) { Status($"Открыто максимум {_panes.Count} панелей."); return; }
        AttachPane(state);
        BuildPaneTabs();
        FocusPane(_panes[^1]);
        ScheduleSave();
    }

    void ClosePane(BrowserPane pane)
    {
        if (_busy || !_navigation.Close(pane.State)) return;
        pane.Read?.Cancel();
        _panes.Remove(pane);
        _paneGrid.Children.Remove(pane.Frame);
        _arrangement = null;
        BuildPaneTabs();
        FocusPane(_panes[_navigation.ActiveIndex]);
        ScheduleSave();
    }

    void ReopenPane()
    {
        if (_navigation.Reopen() is not { } state) { Status("Нет закрытой панели, которую можно вернуть."); return; }
        AttachPane(state);
        BuildPaneTabs();
        FocusPane(_panes[_navigation.Panes.IndexOf(state)]);
        ScheduleSave();
    }

    void BuildPaneTabs()
    {
        _paneTabs.Children.Clear();
        _paneTabs.Children.Add(_placesButton);
        _paneTabs.Children.Add(_newPane);
        _paneTabs.Children.Add(_focusMode);
        for (int i = 0; i < _panes.Count; i++) {
            var pane = _panes[i];
            var body = new DockPanel();
            if (_panes.Count > 1) {
                var close = Command("×", $"Закрыть панель {i + 1}", () => ClosePane(pane), 25);
                close.Padding = new Thickness(5, 0, 5, 0);
                AddDock(body, close, Dock.Right);
            }
            pane.TabTitle = Text("", 11, PanelControls.Silver);
            var label = PanelControls.Button(pane.TabTitle, $"Панель {i + 1}", 25);
            label.Padding = new Thickness(7, 0, 4, 0);
            label.Click += (_, _) => FocusPane(pane);
            body.Children.Add(label);
            pane.Tab = PanelControls.Well(body, new Thickness(1), 8);
            pane.Tab.MaxWidth = 142;
            pane.Tab.Margin = new Thickness(4, 1, 0, 2);
            ConnectDrop(pane, pane.Tab, tab: true);
            _paneTabs.Children.Add(pane.Tab);
            UpdateLocation(pane);
        }
        _newPane.IsEnabled = _panes.Count < StorageNavigation.MaxPanes;
    }

    void ArrangePanes(bool refresh = false)
    {
        if (_busy || _closed || !ReferenceEquals(_page.Content, _files)) return;
        double width = _paneGrid.ActualWidth > 0 ? _paneGrid.ActualWidth : Math.Max(0, Width - 30 - (_places.Visibility == Visibility.Visible ? _places.Width + 5 : 0));
        double height = _paneGrid.ActualHeight > 0 ? _paneGrid.ActualHeight : Math.Max(0, Height - 220);
        var fit = StorageLayout.Fit(width, height, _panes.Count, _navigation.ActiveIndex, _navigation.FocusMode);
        if (_arrangement != fit) {
            _arrangement = fit;
            int rows = (fit.Count + fit.Columns - 1) / fit.Columns;
            _paneGrid.ColumnDefinitions.Clear();
            _paneGrid.RowDefinitions.Clear();
            for (int i = 0; i < fit.Columns; i++) _paneGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < rows; i++) _paneGrid.RowDefinitions.Add(new RowDefinition());
            for (int i = 0; i < _panes.Count; i++) {
                var pane = _panes[i];
                int position = i - fit.Start;
                if (position < 0 || position >= fit.Count) {
                    pane.Frame.Visibility = Visibility.Collapsed;
                    if (pane.Read is { } read) { pane.Dirty = true; read.Cancel(); }
                    continue;
                }
                int column = position % fit.Columns, row = position / fit.Columns;
                int span = position == fit.Count - 1 ? fit.Columns - column : 1;
                Grid.SetRow(pane.Frame, row);
                Grid.SetColumn(pane.Frame, column);
                Grid.SetColumnSpan(pane.Frame, span);
                pane.Frame.Margin = new Thickness(0, 0, column + span == fit.Columns ? 0 : 8, row + 1 == rows ? 0 : 8);
                pane.Frame.Visibility = Visibility.Visible;
            }
        }
        foreach (var pane in _panes) {
            UpdatePaneAccent(pane);
            if (refresh) pane.Dirty = true;
            if (pane.Frame.Visibility == Visibility.Visible && pane.Dirty && (refresh || pane.Read is null))
                _ = ReadPane(pane);
        }
        _focusMode.Background = _navigation.FocusMode ? PanelControls.Chrome : Brushes.Transparent;
        _focusMode.Content = _navigation.FocusMode ? "Сетка" : "Фокус";
        _focusMode.IsEnabled = _panes.Count > 1;
        UpdateSelection();
    }

    void UpdatePaneAccent(BrowserPane pane)
    {
        bool active = pane == _active;
        pane.Frame.BorderBrush = active ? PanelControls.Rim : PanelControls.Outline;
        pane.Tab.Background = active ? PanelControls.Chrome : PanelControls.Ink;
        pane.Tab.BorderBrush = active ? PanelControls.Silver : PanelControls.Outline;
        System.Windows.Automation.AutomationProperties.SetItemStatus(pane.Tab, active ? "Активная панель" : "");
    }

    void SetActive(BrowserPane pane)
    {
        if (_active == pane) return;
        _active = pane;
        _navigation.ActiveIndex = _panes.IndexOf(pane);
        _search.Text = pane.Query;
        _searchScope.Content = pane.SearchGlobal ? "Везде" : "В папке";
        ArrangePanes();
        ScheduleSave();
    }

    void FocusPane(BrowserPane pane)
    {
        SetActive(pane);
        ArrangePanes();
        pane.Items.Focus();
    }

    void CyclePane(int direction)
    {
        int next = (_navigation.ActiveIndex + direction + _panes.Count) % _panes.Count;
        FocusPane(_panes[next]);
    }

    Border BuildPane(BrowserPane pane)
    {
        var root = new DockPanel();
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        pane.Back = PaneButton("←", "Назад · Alt+←", () => Step(pane, -1));
        pane.Forward = PaneButton("→", "Вперёд · Alt+→", () => Step(pane, 1));
        AddDock(toolbar, pane.Back, Dock.Left);
        AddDock(toolbar, pane.Forward, Dock.Left);
        AddDock(toolbar, PaneButton("↑", "На уровень выше · Alt+↑", () => GoUp(pane)), Dock.Left);
        var options = ActionButton("⋯", 27);
        options.Padding = new Thickness(7, 0, 7, 0);
        options.ToolTip = PanelControls.Hint("Действия с панелью");
        options.Click += (_, _) => ShowPaneOptions(pane, options);
        AddDock(toolbar, options, Dock.Right);
        var location = new Grid();
        pane.Breadcrumb = new DockPanel();
        pane.ParentTitle = Text("", 11, PanelControls.Secondary);
        pane.ParentLocation = PanelControls.Button(pane.ParentTitle, "Открыть родительскую папку", 26);
        pane.ParentLocation.MaxWidth = 124;
        pane.ParentLocation.Padding = new Thickness(6, 0, 4, 0);
        pane.ParentLocation.Click += (_, _) => GoUp(pane);
        AddDock(pane.Breadcrumb, pane.ParentLocation, Dock.Left);
        pane.LocationTitle = Text("", 12, PanelControls.Silver);
        pane.LocationButton = PanelControls.Button(pane.LocationTitle, "Путь и родительские папки", 26);
        pane.LocationButton.Padding = new Thickness(6, 0, 5, 0);
        pane.LocationButton.Click += (_, _) => ShowLocationMenu(pane);
        pane.Breadcrumb.Children.Add(pane.LocationButton);
        location.Children.Add(pane.Breadcrumb);
        pane.Address.Margin = new Thickness(5, 0, 3, 0);
        pane.Address.Visibility = Visibility.Collapsed;
        pane.Address.KeyDown += (_, e) => {
            if (e.Key == Key.Enter) { e.Handled = true; Navigate(pane, pane.Address.Text); }
        };
        location.Children.Add(pane.Address);
        toolbar.Children.Add(PanelControls.Well(location, new Thickness(0), 8));
        AddDock(root, toolbar, Dock.Top);

        var refine = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
        pane.SortButton = ActionButton(SortCaption(pane.Order), 26);
        pane.SortButton.Padding = new Thickness(7, 0, 7, 0);
        pane.SortButton.ToolTip = PanelControls.Hint("Сортировка списка");
        pane.SortButton.Click += (_, _) => ShowSortMenu(pane);
        AddDock(refine, pane.SortButton, Dock.Right);
        AddDock(refine, PaneButton("×", "Сбросить фильтр", () => pane.Filter.Clear()), Dock.Right);
        refine.Children.Add(PanelControls.Well(Placeholder(pane.Filter, "Фильтр…"), new Thickness(7, 0, 7, 0), 7));
        pane.Filter.TextChanged += (_, _) => RenderPane(pane);
        AddDock(root, refine, Dock.Top);

        pane.Detail.Margin = new Thickness(4, 5, 2, 0);
        AddDock(root, pane.Detail, Dock.Bottom);
        pane.Items.Resources[typeof(ScrollBar)] = PanelControls.ScrollBar;
        ScrollViewer.SetHorizontalScrollBarVisibility(pane.Items, ScrollBarVisibility.Disabled);
        ScrollViewer.SetPanningMode(pane.Items, PanningMode.VerticalOnly);
        VirtualizingPanel.SetIsVirtualizing(pane.Items, true);
        VirtualizingPanel.SetIsVirtualizingWhenGrouping(pane.Items, true);
        VirtualizingPanel.SetVirtualizationMode(pane.Items, VirtualizationMode.Recycling);
        pane.Items.GroupStyle.Add(new GroupStyle { HeaderTemplate = EntryGroupHeader });
        pane.Items.ItemContainerStyle = EntryStyle;
        pane.Items.ItemTemplate = EntryTemplate;
        pane.Items.SelectionChanged += (_, _) => {
            if (!pane.Rendering && pane == _active) UpdateSelection();
        };
        pane.Items.MouseDoubleClick += (_, e) => {
            if (!pane.TouchSelection && ItemRow(pane, e.OriginalSource)?.Content is BrowserRow row)
                OpenItem(pane, row.Entry);
        };
        pane.Items.KeyDown += (_, e) => {
            if (e.Key == Key.Enter && pane.Items.SelectedItem is BrowserRow row) { e.Handled = true; OpenItem(pane, row.Entry); }
        };
        AddItemMenu(pane);
        ConnectDragSource(pane);
        root.Children.Add(pane.Items);
        var frame = PanelControls.Well(root, new Thickness(6), 11);
        frame.PreviewMouseLeftButtonDown += (_, _) => SetActive(pane);
        frame.GotKeyboardFocus += (_, _) => SetActive(pane);
        frame.SizeChanged += (_, e) => UpdateParentLink(pane, e.NewSize.Width);
        ConnectDrop(pane, frame, tab: false);
        return frame;
    }

    static Button PaneButton(string label, string hint, Action action)
    {
        var button = Command(label, hint, action, 27);
        button.Padding = new Thickness(5, 0, 5, 0);
        button.MinWidth = 23;
        return button;
    }

    void ShowPaneOptions(BrowserPane pane, Button anchor)
    {
        var menu = FolderMenu();
        AddMenuItem(menu, "Новая папка · Ctrl+Shift+N", () => ShowNewFolder(pane));
        AddMenuItem(menu, "Закрепить открытую папку · Ctrl+D", () => PinFolders([pane.Folder]));
        AddMenuItem(menu, "Дублировать в новой панели", () => AddPane(pane.Folder));
        AddMenuItem(menu, "Показать длительность видео", async () => await FillDurations(pane));
        AddMenuItem(menu, "Пакетное переименование · F2", () => ShowBatchRename(pane, Selected(pane)));
        AddMenuItem(menu, pane.State.ShowHidden ? "Скрыть скрытые элементы" : "Показать скрытые элементы", () => {
            pane.State.ShowHidden = !pane.State.ShowHidden;
            _ = ReadPane(pane);
            ScheduleSave();
        });
        AddMenuItem(menu, "Открыть в Проводнике", () => Try(() => { IslandWindow.ShowFolder(pane.Folder); DismissOnOutside(); }));
        AddMenuItem(menu, "Копировать адрес", () => CopyPath(pane.Folder));
        OpenMenu(anchor, menu);
    }

    void ShowLocationMenu(BrowserPane pane)
    {
        var menu = FolderMenu();
        AddMenuItem(menu, "Ввести адрес · Ctrl+L", () => EditAddress(pane));
        AddMenuItem(menu, "Копировать адрес", () => CopyPath(pane.Folder));
        var ancestors = new Stack<string>();
        for (var parent = Directory.GetParent(pane.Folder); parent is not null; parent = parent.Parent)
            ancestors.Push(parent.FullName);
        if (ancestors.Count != 0) menu.Items.Add(new Separator());
        foreach (var path in ancestors)
            AddMenuItem(menu, PathCaption(path), () => Navigate(pane, path));
        var recent = _navigation.Recent.Where(path => !path.Equals(pane.Folder, StringComparison.OrdinalIgnoreCase)).Take(4).ToArray();
        if (recent.Length != 0) menu.Items.Add(new Separator());
        foreach (var path in recent)
            AddMenuItem(menu, "Недавняя · " + PathCaption(path), () => Navigate(pane, path), path);
        OpenMenu(pane.LocationButton, menu);
    }

    void EditAddress(BrowserPane pane)
    {
        pane.EditingAddress = true;
        pane.Breadcrumb.Visibility = Visibility.Collapsed;
        pane.Address.Visibility = Visibility.Visible;
        pane.Address.Text = pane.Folder;
        pane.Address.Focus();
        pane.Address.SelectAll();
    }

    void CloseAddress(BrowserPane pane)
    {
        pane.EditingAddress = false;
        pane.Address.Visibility = Visibility.Collapsed;
        pane.Breadcrumb.Visibility = Visibility.Visible;
    }

    static string PathCaption(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length == 0 ? path.TrimEnd('\\', '/') : name;
    }

    void UpdateLocation(BrowserPane pane)
    {
        if (!pane.EditingAddress) pane.Address.Text = pane.Folder;
        pane.LocationTitle.Text = "›  " + PathCaption(pane.Folder);
        pane.LocationButton.ToolTip = PanelControls.Hint(pane.Folder);
        UpdateParentLink(pane, pane.Frame.ActualWidth);
        pane.TabTitle.Text = $"{_panes.IndexOf(pane) + 1} · {PathCaption(pane.Folder)}";
        pane.Tab.ToolTip = PanelControls.Hint(pane.Folder);
        pane.Back.IsEnabled = pane.History.CanGoBack;
        pane.Forward.IsEnabled = pane.History.CanGoForward;
        pane.SortButton.Content = SortCaption(pane.Order);
    }

    static void UpdateParentLink(BrowserPane pane, double width)
    {
        var parent = Directory.GetParent(pane.Folder);
        pane.ParentLocation.Visibility = width >= 400 && parent is not null ? Visibility.Visible : Visibility.Collapsed;
        if (pane.ParentLocation.Visibility != Visibility.Visible || parent is null) return;
        pane.ParentTitle.Text = PathCaption(parent.FullName);
        pane.ParentLocation.ToolTip = PanelControls.Hint(parent.FullName);
    }

    static string SortCaption(StorageOrder order) => order.Sort switch {
        StorageSort.Added => order.Reverse ? "Добавлено ↑" : "Добавлено ↓",
        StorageSort.Newest => order.Reverse ? "Изменено ↑" : "Изменено ↓",
        StorageSort.Size => order.Reverse ? "Размер ↑" : "Размер ↓",
        StorageSort.Type => order.Reverse ? "Тип ↓" : "Тип ↑",
        _ => order.Reverse ? "Имя ↓" : "Имя ↑"
    };

    void ShowSortMenu(BrowserPane pane)
    {
        var menu = FolderMenu();
        var current = pane.Order;
        void ApplyOrder(StorageOrder order)
        {
            pane.Order = order;
            pane.SortButton.Content = SortCaption(order);
            RenderPane(pane);
            ScheduleSave();
        }
        foreach (var (sort, caption) in new[] {
            (StorageSort.Name, "Имя · от А до Я"), (StorageSort.Added, "Дата добавления · по группам"),
            (StorageSort.Newest, "Дата изменения · сначала новые"),
            (StorageSort.Size, "Размер · сначала крупные"), (StorageSort.Type, "Тип файла")
        }) AddMenuItem(menu, (current.Sort == sort ? "✓  " : "") + caption, () => ApplyOrder(new(sort)));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "Обратный порядок", () => ApplyOrder(current with { Reverse = !current.Reverse }));
        OpenMenu(pane.SortButton, menu);
    }

    void Navigate(BrowserPane pane, string path, string? selectPath = null)
    {
        if (_busy || _closed) return;
        Try(() => {
            string input = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (input.Length == 0) throw new ArgumentException("Введи адрес папки.");
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input, pane.Folder));
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Папка недоступна: " + full);
            pane.History.Visit(full);
            ShowFolder(pane, selectPath);
        });
    }

    void GoUp(BrowserPane pane)
    {
        if (Directory.GetParent(pane.Folder) is { } parent) Navigate(pane, parent.FullName, pane.Folder);
        else Status("Открыта корневая папка.");
    }

    void Step(BrowserPane pane, int direction)
    {
        if (_busy) return;
        if (pane.History.TryStep(direction)) ShowFolder(pane);
        else Status("В этом направлении нет доступной папки.");
    }

    void ShowFolder(BrowserPane pane, string? selectPath = null)
    {
        _navigation.Visit(pane.Folder);
        RefreshRecents();
        ScheduleSave();
        pane.Query = "";
        pane.SelectPath = selectPath;
        if (pane == _active) _search.Clear();
        CloseAddress(pane);
        ResetListing(pane);
        UpdateLocation(pane);
        if (!ReferenceEquals(_page.Content, _files)) ShowView("Файлы");
        else if (pane.Frame.Visibility == Visibility.Visible) _ = ReadPane(pane);
        if (pane == _active) pane.Items.Focus();
    }

    void Search()
    {
        if (_busy) return;
        string query = _search.Text.Trim();
        if (query.Length == 0) { ClearSearch(); return; }
        _active.Query = query;
        ResetListing(_active);
        _ = ReadPane(_active);
    }

    void ClearSearch()
    {
        if (_busy) return;
        _search.Clear();
        if (_active.Query.Length == 0) return;
        _active.Query = "";
        ResetListing(_active);
        _ = ReadPane(_active);
    }

    static void ResetListing(BrowserPane pane)
    {
        pane.Read?.Cancel();
        pane.Listing = null;
        pane.Dirty = true;
        pane.Items.ItemsSource = null;
        pane.Filter.Clear();
    }

    void CancelReads()
    {
        foreach (var pane in _panes)
            if (pane.Read is { } read) { pane.Dirty = true; read.Cancel(); }
    }

    async Task ReadPane(BrowserPane pane)
    {
        pane.Read?.Cancel();
        using var read = new CancellationTokenSource();
        pane.Read = read;
        pane.Dirty = false;
        string folder = pane.Folder, query = pane.Query;
        bool global = pane.SearchGlobal, hidden = pane.State.ShowHidden;
        UpdateLocation(pane);
        pane.Detail.Text = query.Length > 0 ? "Ищу по имени…" : "Читаю папку…";
        try {
            var listing = await Task.Run(() => query.Length == 0
                ? StorageCatalog.List(folder, read.Token, hidden)
                : StorageCatalog.Search(global ? StorageCatalog.SearchRoots() : [folder], query, read.Token, hidden), read.Token);
            if (read.IsCancellationRequested || _closed || !ReferenceEquals(_page.Content, _files)) return;
            pane.Listing = listing;
            RenderPane(pane);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            if (!read.IsCancellationRequested && !_closed) {
                pane.Listing = null;
                pane.Items.ItemsSource = null;
                pane.Detail.Text = "Папка недоступна · F5 обновить";
                pane.Detail.ToolTip = PanelControls.Hint(ex.Message);
            }
        }
        finally {
            if (ReferenceEquals(pane.Read, read)) {
                pane.Read = null;
                if (pane.Dirty && !_busy && !_closed && pane.Frame.IsVisible && ReferenceEquals(_page.Content, _files))
                    _ = ReadPane(pane);
            }
        }
    }

    void RenderPane(BrowserPane pane)
    {
        if (pane.Listing is not { } listing) return;
        var selected = Selected(pane).Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pane.SelectPath is { } selectPath) { selected.Add(selectPath); pane.SelectPath = null; }
        bool results = pane.Query.Length > 0;
        var order = pane.Order;
        bool groupByDate = order.Sort == StorageSort.Added;
        var today = DateTime.Today;
        var rows = StorageCatalog.FilterAndSort(listing.Entries, pane.Filter.Text, order.Sort, order.Reverse)
            .Select(entry => new BrowserRow(entry, RowDetail(entry, order.Sort), results ? Path.GetDirectoryName(entry.Path) : null,
                groupByDate ? StorageCatalog.AddedGroup(entry.Added, today) : null)).ToArray();
        pane.Rendering = true;
        try {
            if (groupByDate) {
                var view = new ListCollectionView(rows);
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BrowserRow.DateGroup)));
                pane.Items.ItemsSource = view;
            } else pane.Items.ItemsSource = rows;
            foreach (var row in rows.Where(row => selected.Contains(row.Path))) pane.Items.SelectedItems.Add(row);
            if (pane.Items.SelectedItem is { } first) pane.Items.ScrollIntoView(first);
        }
        finally { pane.Rendering = false; }
        pane.Detail.Text = rows.Length == 0
            ? results || pane.Filter.Text.Length != 0 ? "Совпадений нет" : "Папка пуста"
            : $"{(results ? "Найдено" : "Элементов")} · {rows.Length}" +
                (rows.Length != listing.Entries.Length ? $" из {listing.Entries.Length}" : "");
        if (listing.Limited) pane.Detail.Text += " · есть ещё";
        pane.Detail.ToolTip = listing.Limited ? PanelControls.Hint("Показана часть результатов. Уточни запрос или открой подпапку.") : null;
        if (pane == _active) UpdateSelection();
    }

    string RowDetail(StorageEntry entry, StorageSort sort) => _durations.TryGetValue(entry, out var duration) ? VideoTime(duration) : sort switch {
        StorageSort.Newest => entry.Modified.ToLocalTime().ToString("dd.MM.yy"),
        StorageSort.Type when !entry.Folder => Path.GetExtension(entry.Name).TrimStart('.').ToUpperInvariant(),
        _ => entry.Folder ? "" : entry.OnlineOnly ? "Онлайн" : StorageCatalog.Size(entry.Bytes)
    };

    void OpenItem(BrowserPane pane, StorageEntry entry)
    {
        if (_busy) return;
        if (entry.Folder) Navigate(pane, entry.Path);
        else Launch(entry.Path);
    }

    static ListBoxItem? ItemRow(BrowserPane pane, object source) => source is DependencyObject element
        ? ItemsControl.ContainerFromElement(pane.Items, element) as ListBoxItem : null;

    static StorageEntry[] Selected(BrowserPane pane) =>
        pane.Items.SelectedItems.OfType<BrowserRow>().Select(row => row.Entry).ToArray();

    static string SelectedFolderOrCurrent(BrowserPane pane) => Selected(pane) is [ { Folder: true } folder ] ? folder.Path : pane.Folder;

    void ToggleSelectionMode()
    {
        _active.TouchSelection = !_active.TouchSelection;
        _active.Items.SelectionMode = _active.TouchSelection ? SelectionMode.Multiple : SelectionMode.Extended;
        _active.Items.Tag = _active.TouchSelection ? "touch" : null;
        UpdateSelection();
        Status(_active.TouchSelection ? "Касайся элементов, чтобы отмечать их. Нажми «Выбор», чтобы выйти." : "");
    }

    void UpdateSelection()
    {
        var items = Selected(_active);
        bool hasTransfer = items.Any(item => item.Folder || !item.OnlineOnly);
        _copy.IsEnabled = _move.IsEnabled = hasTransfer;
        _preview.IsEnabled = items.Length == 1;
        _selectMode.Background = _active.TouchSelection ? PanelControls.Chrome : Brushes.Transparent;
        _selectMode.Content = _active.TouchSelection ? "Готово" : "Выбор";
        string hint = $"Панель {_navigation.ActiveIndex + 1}/{_panes.Count}";
        if (_arrangement is { } fit && fit.Count < _panes.Count) hint += $" · видно {fit.Count}";
        if (items.Length != 0) {
            long bytes = items.Where(item => !item.Folder).Sum(item => item.Bytes);
            hint += $" · выбрано {items.Length}" + (bytes > 0 ? $" · {StorageCatalog.Size(bytes)}" : "");
        }
        _selection.Text = hint;
    }

    static readonly DataTemplate EntryGroupHeader = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Border Margin="2,6,2,2" Padding="5,3" BorderThickness="0,0,0,1" BorderBrush="#2F3037">
            <TextBlock Text="{Binding Name}" FontWeight="SemiBold" FontSize="11" Foreground="#B7B9C1"/>
          </Border>
        </DataTemplate>
        """);

    static readonly Style EntryStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ListBoxItem">
          <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
          <Setter Property="Template"><Setter.Value>
            <ControlTemplate TargetType="ListBoxItem">
              <Border x:Name="Row" CornerRadius="6" Padding="6,4" Background="Transparent" BorderThickness="1" BorderBrush="Transparent">
                <ContentPresenter/>
              </Border>
              <ControlTemplate.Triggers>
                <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource AncestorType=ListBox}}" Value="touch">
                  <Setter TargetName="Row" Property="MinHeight" Value="36"/>
                </DataTrigger>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Row" Property="Background" Value="#242429"/></Trigger>
                <Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="#303643"/></Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Row" Property="BorderBrush" Value="#67768C"/></Trigger>
                <Trigger Property="Tag" Value="drop-target">
                  <Setter TargetName="Row" Property="Background" Value="#364658"/>
                  <Setter TargetName="Row" Property="BorderBrush" Value="#A1BBD7"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
          </Setter.Value></Setter>
        </Style>
        """);

    static readonly DataTemplate EntryTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <DockPanel>
            <TextBlock x:Name="Marker" DockPanel.Dock="Left" Text="○" Width="18" Visibility="Collapsed"
                       Foreground="#AEBACC" VerticalAlignment="Center"/>
            <TextBlock DockPanel.Dock="Left" FontFamily="Segoe Fluent Icons" Text="{Binding Icon}"
                       Foreground="#AEBACC" Width="20" VerticalAlignment="Center"/>
            <TextBlock x:Name="Detail" DockPanel.Dock="Right" Text="{Binding Detail}" MaxWidth="72" Margin="6,0,0,0"
                       FontSize="10" Foreground="#A9ABB5" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
            <StackPanel>
              <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" ToolTip="{Binding Path}"/>
              <TextBlock x:Name="Parent" Text="{Binding Parent}" TextTrimming="CharacterEllipsis" FontSize="10" Foreground="#A9ABB5"/>
            </StackPanel>
          </DockPanel>
          <DataTemplate.Triggers>
            <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource AncestorType=ListBox}}" Value="touch">
              <Setter TargetName="Marker" Property="Visibility" Value="Visible"/>
            </DataTrigger>
            <DataTrigger Binding="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" Value="True">
              <Setter TargetName="Marker" Property="Text" Value="✓"/>
            </DataTrigger>
            <DataTrigger Binding="{Binding Parent}" Value="{x:Null}"><Setter TargetName="Parent" Property="Visibility" Value="Collapsed"/></DataTrigger>
            <DataTrigger Binding="{Binding Detail}" Value=""><Setter TargetName="Detail" Property="Visibility" Value="Collapsed"/></DataTrigger>
          </DataTemplate.Triggers>
        </DataTemplate>
        """);
}
