using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace IslandBar;

sealed partial class StorageWindow
{
    readonly DockPanel _places = new() { Width = 190, Margin = new Thickness(0, 0, 5, 0) };
    readonly StackPanel _sidebar = new();
    Border? _recentSection;
    StackPanel? _recentRows;
    ScrollViewer _placesScroll = null!;
    bool _compactPlaces;
    double BrowserWidth => ActualWidth > 0 ? ActualWidth : Width;
    bool CompactPlaces => BrowserWidth < 760;
    double MaxPlacesWidth => Math.Clamp(BrowserWidth - 315, 132, 320);

    static readonly ControlTemplate ResizeGrip = (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Thumb">
          <Grid Background="Transparent">
            <Border x:Name="Line" Width="1" Margin="0,6" CornerRadius="1" Background="#2F3037"/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Line" Property="Background" Value="#B7B9C1"/></Trigger>
            <Trigger Property="IsDragging" Value="True"><Setter TargetName="Line" Property="Background" Value="#E2E4EA"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    void BuildPlaces(DockPanel body)
    {
        var resize = new Thumb { Width = 9, Cursor = Cursors.SizeWE, Template = ResizeGrip,
            ToolTip = PanelControls.Hint("Потяни край, чтобы изменить ширину панели") };
        resize.DragDelta += (_, e) => {
            _navigation.PlacesWidth = Math.Clamp(_places.Width + e.HorizontalChange, 132, MaxPlacesWidth);
            ApplyPlaces();
        };
        resize.DragCompleted += (_, _) => ScheduleSave();
        AddDock(_places, resize, Dock.Right);
        var add = Command("+ Быстрая папка", "Добавить в быстрые папки", ChooseQuickFolders, 30);
        add.Margin = new Thickness(0, 0, 0, 5);
        PanelControls.FinishTile(add);
        AddDock(_places, add, Dock.Top);
        _placesScroll = PanelControls.Scroller(_sidebar, double.PositiveInfinity);
        ScrollViewer.SetPanningMode(_placesScroll, PanningMode.VerticalOnly);
        _places.Children.Add(_placesScroll);
        AddDock(body, _places, Dock.Left);
    }

    void ApplyPlaces()
    {
        bool show = CompactPlaces ? _compactPlaces : _navigation.ShowPlaces;
        _places.Width = Math.Min(_navigation.PlacesWidth, MaxPlacesWidth);
        _places.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _placesButton.Background = show ? PanelControls.Chrome : Brushes.Transparent;
    }

    void TogglePlaces()
    {
        if (CompactPlaces) _compactPlaces = !_compactPlaces;
        else { _navigation.ShowPlaces = !_navigation.ShowPlaces; ScheduleSave(); }
        ApplyPlaces();
    }


    static Border PlaceGrip(string hint, double width = 23) => new() {
        Width = width, Background = Brushes.Transparent, Cursor = Cursors.SizeAll,
        Child = new TextBlock { Text = "⠿", FontSize = 15, Foreground = PanelControls.Secondary,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        ToolTip = PanelControls.Hint(hint)
    };

    PlaceReorder ReorderPlaces(Panel rows, string group) => new(rows, ids => {
        _navigation.SavePlacesOrder(group, ids);
        ScheduleSave();
    }, Status) { Scroll = _placesScroll };

    (Border Frame, StackPanel Rows) AddPlacesSection(string title, string hint, StoragePlaceSections section, PlaceReorder reorder)
    {
        var label = new DockPanel();
        var arrow = Text("", 12, PanelControls.Secondary);
        AddDock(label, arrow, Dock.Right);
        label.Children.Add(PanelControls.Eyebrow(title));
        var header = PanelControls.Button(label, title, 28);
        header.ToolTip = PanelControls.Hint(hint);
        var headerLine = new DockPanel();
        var grip = PlaceGrip("Перетащи, чтобы переместить раздел");
        AddDock(headerLine, grip, Dock.Right);
        headerLine.Children.Add(header);
        var rows = new StackPanel();
        var body = new StackPanel { Children = { headerLine, rows } };
        var frame = new Border { Child = body, Tag = section.ToString(), Margin = new Thickness(0, 4, 0, 0),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
        void Update()
        {
            bool collapsed = _navigation.CollapsedPlaces.HasFlag(section);
            arrow.Text = collapsed ? "›" : "⌄";
            rows.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetItemStatus(header, collapsed ? "Свёрнуто" : "Раскрыто");
        }
        header.Click += (_, _) => { _navigation.CollapsedPlaces ^= section; Update(); ScheduleSave(); };
        Update();
        var menu = FolderMenu();
        AddOrderCommands(menu, _sidebar, frame, reorder, "Раздел выше", "Раздел ниже");
        frame.ContextMenu = menu;
        _sidebar.Children.Add(frame);
        reorder.Bind(frame, grip);
        return (frame, rows);
    }

    void AddOrderCommands(ContextMenu menu, Panel rows, Border row, PlaceReorder reorder, string up, string down)
    {
        AddMove(up, -1);
        AddMove(down, 1);
        void AddMove(string caption, int offset)
        {
            int Target()
            {
                for (int index = rows.Children.IndexOf(row) + offset; index >= 0 && index < rows.Children.Count; index += offset)
                    if (rows.Children[index].Visibility == Visibility.Visible) return index;
                return -1;
            }
            AddMenuItem(menu, caption, () => reorder.Move(row, Target()));
            var command = (MenuItem)menu.Items[^1];
            menu.Opened += (_, _) => command.IsEnabled = Target() >= 0;
        }
    }

    Border AddSortablePlace(StackPanel rows, PlaceReorder reorder, string id, Button link, Action<ContextMenu>? configure = null)
    {
        var grip = PlaceGrip("Перетащи, чтобы изменить порядок");
        var content = new DockPanel();
        AddDock(content, grip, Dock.Right);
        content.Children.Add(link);
        var row = new Border { Child = content, Tag = id, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7) };
        var menu = FolderMenu();
        configure?.Invoke(menu);
        if (menu.Items.Count != 0) menu.Items.Add(new Separator());
        AddOrderCommands(menu, rows, row, reorder, "Выше в списке", "Ниже в списке");
        row.ContextMenu = menu;
        rows.Children.Add(row);
        reorder.Bind(row, grip);
        return row;
    }

    void BuildSidebar(string? showPath = null)
    {
        if (showPath is not null && _navigation.CollapsedPlaces.HasFlag(StoragePlaceSections.Quick)) {
            _navigation.CollapsedPlaces &= ~StoragePlaceSections.Quick;
            ScheduleSave();
        }
        _sidebar.Children.Clear();
        var reorder = ReorderPlaces(_sidebar, "Sections");
        var sections = new[] {
            (StoragePlaceSections.Quick, "БЫСТРЫЕ ПАПКИ", "Закреплённые папки"),
            (StoragePlaceSections.Drives, "ХРАНИЛИЩА", "Диски, OneDrive и корзина"),
            (StoragePlaceSections.Workspaces, "ПОДБОРКИ ПАПОК", "Группы папок для монтажа, работы, игр и архива"),
            (StoragePlaceSections.Recent, "НЕДАВНИЕ", "Недавно открытые папки")
        };
        foreach (var (section, title, hint) in _navigation.OrderPlaces("Sections", sections, item => item.Item1.ToString())) {
            var (frame, rows) = AddPlacesSection(title, hint, section, reorder);
            switch (section) {
                case StoragePlaceSections.Quick: BuildQuickPlaces(rows, showPath); break;
                case StoragePlaceSections.Drives: BuildDrivePlaces(rows); break;
                case StoragePlaceSections.Workspaces: BuildWorkspacePlaces(rows); break;
                case StoragePlaceSections.Recent: _recentSection = frame; _recentRows = rows; break;
            }
        }
        RefreshRecents();
    }

    void BuildQuickPlaces(StackPanel quick, string? showPath)
    {
        var list = new StackPanel();
        quick.Children.Add(list);
        var reorder = new PlaceReorder(list, QuickFolders.SaveOrder, Status) { Scroll = _placesScroll };
        var custom = QuickFolders.Custom().Select(folder => folder.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in QuickFolders.All()) {
            var link = Link(folder.Glyph, folder.Name, () => OpenPlace(folder.Path), folder.Path);
            bool added = custom.Contains(folder.Path);
            var row = AddSortablePlace(list, reorder, folder.Path, link, menu => {
                AddFolderActions(menu, folder.Path, null, removable: added);
                if (!added) AddMenuItem(menu, "Скрыть из быстрых папок", () => Try(() => {
                    QuickFolders.SetHidden(folder.Path, true);
                    BuildSidebar();
                    Status("Папка скрыта из списка.");
                }));
            });
            if (folder.Path.Equals(showPath, StringComparison.OrdinalIgnoreCase))
                row.Loaded += (_, _) => row.BringIntoView();
        }
        var hidden = QuickFolders.Hidden();
        if (hidden.Length == 0) return;
        var restore = ActionButton($"Скрытые папки · {hidden.Length}", 29);
        restore.Click += (_, _) => {
            var menu = FolderMenu();
            foreach (var folder in QuickFolders.Hidden()) AddMenuItem(menu, "Показать · " + folder.Name, () => Try(() => {
                QuickFolders.SetHidden(folder.Path, false);
                BuildSidebar(folder.Path);
                Status("Папка снова в быстрых.");
            }));
            OpenMenu(restore, menu);
        };
        quick.Children.Add(restore);
    }

    void BuildDrivePlaces(StackPanel rows)
    {
        const string group = nameof(StoragePlaceSections.Drives);
        var entries = StorageCatalog.Volumes().Select(volume =>
            new StoragePlace(volume.Root.TrimEnd('\\') + "  " + volume.Name, volume.Root, "\uEDA2")).ToList();
        if (Directory.Exists(StorageCatalog.Cloud)) entries.Add(new("OneDrive", StorageCatalog.Cloud, "\uE753"));
        const string bin = "shell:RecycleBinFolder";
        entries.Add(new("Корзина", bin, "\uE74D"));
        var reorder = ReorderPlaces(rows, group);
        foreach (var entry in _navigation.OrderPlaces(group, entries, entry => entry.Path)) {
            var link = entry.Path == bin
                ? Link(entry.Glyph, entry.Name, () => Launch(bin))
                : Link(entry.Glyph, entry.Name, () => OpenPlace(entry.Path), entry.Path);
            AddSortablePlace(rows, reorder, entry.Path, link, menu => {
                if (entry.Path != bin) AddFolderActions(menu, entry.Path, null);
            });
        }
    }

    void BuildWorkspacePlaces(StackPanel rows)
    {
        const string group = nameof(StoragePlaceSections.Workspaces);
        var reorder = ReorderPlaces(rows, group);
        foreach (var space in _navigation.OrderPlaces(group, StorageCatalog.Spaces(), space => space.Name))
            AddSortablePlace(rows, reorder, space.Name, Link(space.Glyph, space.Name, () => ShowSpace(space),
                $"Открыть подборку · папок {space.Places.Length}"));
    }

    void OpenPlace(string path)
    {
        Navigate(_active, path);
        if (CompactPlaces && _compactPlaces) { _compactPlaces = false; ApplyPlaces(); }
    }

    void RefreshRecents()
    {
        if (_recentSection is null || _recentRows is not { } rows) return;
        _recentSection.Visibility = _navigation.Recent.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        rows.Children.Clear();
        const string group = nameof(StoragePlaceSections.Recent);
        var reorder = ReorderPlaces(rows, group);
        foreach (string path in _navigation.OrderPlaces(group, _navigation.Recent, path => path))
            AddSortablePlace(rows, reorder, path, Link("\uE823", PathCaption(path), () => OpenPlace(path), path),
                menu => AddFolderActions(menu, path, null));
    }
}
