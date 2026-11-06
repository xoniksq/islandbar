using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace IslandBar;

sealed partial class StorageWindow : Window
{
    readonly StorageTransfers _transfers;
    readonly StorageQueue _queue;
    readonly StorageNavigation _navigation;
    readonly string? _layoutPath;
    readonly DispatcherTimer _saveLayout = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Border _frame;
    readonly Grid _pageLayer = new();
    readonly ContentControl _page = new();
    readonly TextBlock _status = Text("", 12, PanelControls.Secondary);
    readonly Dictionary<string, Button> _sections = [];
    readonly Button _desktop = ActionButton("Скрыть значки");
    ContextMenu? _openFolderMenu;
    Action? _backFromDetail;
    UIElement? _inbox;
    string _view = "Файлы";
    bool _initialized, _closed, _closing, _busy, _dialog;
    int _pageRevision;

    public StorageWindow(Rect bounds, StorageNavigation navigation, StorageTransfers? transfers = null, string? layoutPath = null,
        StorageQueue? queue = null)
    {
        _transfers = transfers ?? new(Path.Combine(Program.Folder, "storage-journal.json"));
        _queue = queue ?? new StorageQueue(_transfers);
        _queue.Changed += QueueChanged;
        _navigation = navigation;
        _layoutPath = layoutPath;
        Title = "IslandBar — Хранилище";
        FontFamily = new FontFamily("Segoe UI");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome {
            CaptionHeight = 0, GlassFrameThickness = new Thickness(0), ResizeBorderThickness = new Thickness(6),
            CornerRadius = new CornerRadius(0)
        });
        MinWidth = Math.Min(420, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(360, SystemParameters.WorkArea.Height);
        ShowInTaskbar = true;
        Topmost = !_navigation.KeepOpen;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
        var shell = new DockPanel();
        _frame = PanelControls.Frame(shell, new Thickness(14, 12, 14, 12));
        Content = _frame;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        AddDock(header, Command("✕", "Закрыть хранилище", Dismiss), Dock.Right);
        var maximize = Command("□", "Развернуть или восстановить окно", ToggleWindowSize);
        AddDock(header, maximize, Dock.Right);
        var pin = Command("◇", "Оставлять хранилище открытым при клике снаружи", () => {
            _navigation.KeepOpen = !_navigation.KeepOpen;
            Topmost = !_navigation.KeepOpen;
            Status(_navigation.KeepOpen ? "Хранилище останется открытым при переключении приложений." : "Клик снаружи закроет хранилище.");
            ScheduleSave();
        });
        void UpdatePin() {
            pin.Content = _navigation.KeepOpen ? "◆" : "◇";
            pin.Background = _navigation.KeepOpen ? PanelControls.Chrome : Brushes.Transparent;
            System.Windows.Automation.AutomationProperties.SetItemStatus(pin, _navigation.KeepOpen ? "Закреплено" : "Откреплено");
        }
        pin.Click += (_, _) => UpdatePin();
        UpdatePin();
        AddDock(header, pin, Dock.Right);
        AddDock(header, Command("?", "Горячие клавиши и жесты", ShowHelp), Dock.Right);
        var title = new StackPanel { Background = Brushes.Transparent, Margin = new Thickness(3, 0, 0, 0) };
        title.Children.Add(PanelControls.Eyebrow("ОСТРОВОК  /  ФАЙЛОВЫЙ ЦЕНТР"));
        title.Children.Add(Text("Хранилище", 25, weight: FontWeights.SemiBold));
        title.MouseLeftButtonDown += (_, e) => {
            if (e.ClickCount == 2) ToggleWindowSize();
            else if (WindowState == WindowState.Normal && e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        header.Children.Add(title);
        AddDock(shell, header, Dock.Top);

        var nav = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
        AddDock(nav, Command("↻", "Обновить · F5", Refresh), Dock.Right);
        var sections = new WrapPanel();
        foreach (string name in new[] { "Файлы", "Карта", "Разбор", "Журнал", "Очередь", "Проекты" }) {
            var tab = Command(name, name, () => ShowView(name));
            _sections.Add(name, tab);
            sections.Children.Add(tab);
        }
        nav.Children.Add(sections);
        AddDock(shell, nav, Dock.Top);
        _status.Margin = new Thickness(4, 4, 4, 0);
        _status.MinHeight = 17;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(_status, System.Windows.Automation.AutomationLiveSetting.Polite);
        var footer = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        footer.Children.Add(_status);
        AddDock(shell, footer, Dock.Bottom);
        _pageLayer.Children.Add(_page);
        shell.Children.Add(_pageLayer);

        BuildBrowser();
        _saveLayout.Tick += (_, _) => SaveLayout();
        SizeChanged += (_, _) => {
            ApplyPlaces();
            ScheduleSave();
        };
        StateChanged += (_, _) => maximize.Content = WindowState == WindowState.Maximized ? "❐" : "□";
        Activated += (_, _) => QueueChanged();
        Loaded += (_, _) => {
            if (_initialized) return;
            _initialized = true;
            ChromeMotion.Enter(_frame);
            ShowView(_view);
            _active.Items.Focus();
        };
        PreviewKeyDown += (_, e) => OnShortcut(e);
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) => {
            _closed = true;
            _queue.Changed -= QueueChanged;
            CancelReads();
            ClosePreview();
            SaveLayout();
        };
    }

    public bool TransferRunning => _busy;
    public void OpenProjects() => ShowView("Проекты");
    public void OpenLocation(string path) => Navigate(_active, path);
    public bool KeepOpen => _navigation.KeepOpen;

    public bool ContainsScreenPoint(int x, int y)
    {
        static bool Contains(nint handle, int x, int y) => handle != 0 && Native.GetWindowRect(handle, out var bounds) &&
            x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;
        return IsVisible && (Contains(new WindowInteropHelper(this).Handle, x, y) ||
            _openFolderMenu is { IsOpen: true } menu && PresentationSource.FromVisual(menu) is HwndSource source &&
            Contains(source.Handle, x, y));
    }

    public void DismissOnOutside()
    {
        if (!_navigation.KeepOpen && !_busy) Dismiss();
    }

    public void Dismiss()
    {
        if (_closing || _closed || _dialog) return;
        if (_busy) { Status("Дождись завершения текущего элемента."); return; }
        _closing = true;
        if (IsVisible) ChromeMotion.Exit(_frame, Close);
        else Close();
    }

    void ToggleWindowSize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void ScheduleSave()
    {
        if (_layoutPath is null || !_initialized || _closed) return;
        _saveLayout.Stop();
        _saveLayout.Start();
    }

    void SaveLayout()
    {
        _saveLayout.Stop();
        if (_layoutPath is null) return;
        if (WindowState == WindowState.Normal && Width >= MinWidth && Height >= MinHeight) {
            _navigation.Width = Width;
            _navigation.Height = Height;
        }
        _navigation.Save(_layoutPath);
    }

    void Status(string text)
    {
        _status.Text = text;
        _status.ToolTip = text.Length == 0 ? null : PanelControls.Hint(text);
    }

    void Refresh()
    {
        if (_busy) return;
        if (_view == "Разбор") _inbox = null;
        ShowView(_view);
    }

    void ShowView(string view)
    {
        if (_busy || _closed) return;
        int revision = ++_pageRevision;
        _view = view;
        _backFromDetail = null;
        ClosePreview();
        foreach (var (name, tab) in _sections) tab.Background = name == view ? PanelControls.Chrome : Brushes.Transparent;
        Status("");
        if (view != "Файлы") CancelReads();
        switch (view) {
            case "Файлы":
                _page.Content = _files;
                BuildSidebar();
                ApplyPlaces();
                ArrangePanes(refresh: true);
                break;
            case "Карта": ShowMap(); break;
            case "Разбор": _ = ShowInbox(revision); break;
            case "Журнал": _ = ShowJournal(revision); break;
            case "Очередь": ShowQueuePage(); break;
            case "Проекты": ShowProjects(); break;
        }
    }

    void ShowDetail(UIElement content, Action back)
    {
        CancelReads();
        _backFromDetail = back;
        _page.Content = content;
    }

    void ShowHelp() => ShowOverlay("Горячие клавиши", null, new StackPanel { Children = {
        Text("Ctrl+T  ·  новая панель       Ctrl+Tab  ·  следующая панель", 13, wrap: true),
        Text("Ctrl+W  ·  закрыть панель       Ctrl+Shift+T  ·  вернуть закрытую", 13, wrap: true),
        Text("Ctrl+L  ·  адрес       Ctrl+F  ·  поиск       Ctrl+I  ·  фильтр", 13, wrap: true),
        Text("Alt+D  ·  адрес       Alt+P  ·  предпросмотр       Ctrl+R  ·  обновить", 13, wrap: true),
        Text("F2  ·  переименовать       F5  ·  обновить       F3  ·  предпросмотр", 13, wrap: true),
        Text("Ctrl+C / X / V  ·  копировать, вырезать и вставить", 13, wrap: true),
        Text("Ctrl+Z  ·  отменить последний перенос или переименование       F1  ·  помощь", 13, wrap: true),
        Text("Delete  ·  отправить выбранное в корзину", 13, wrap: true),
        Text("Зажми ЛКМ и перетащи на папку, панель или её вкладку. Ctrl при сбросе — копирование.", 13, PanelControls.Secondary, wrap: true)
    }});

    ContextMenu FolderMenu()
    {
        var menu = IslandWindow.CreateMenu();
        menu.Opened += (_, _) => _openFolderMenu = menu;
        menu.Closed += (_, _) => { if (_openFolderMenu == menu) _openFolderMenu = null; };
        return menu;
    }

    void OpenMenu(Button anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    static void AddMenuItem(ContextMenu menu, string title, Action action, string? hint = null)
    {
        var item = new MenuItem { Header = title };
        if (hint is not null) item.ToolTip = PanelControls.Hint(hint);
        item.Click += (_, _) => { menu.IsOpen = false; action(); };
        menu.Items.Add(item);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        _page.IsHitTestVisible = !busy;
        KeyboardNavigation.SetTabNavigation(_page, busy ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
        if (busy && _page.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        foreach (var tab in _sections.Values) tab.IsEnabled = !busy;
    }

    void Launch(string path) => Try(() => {
        if (path.StartsWith("shell:", StringComparison.Ordinal))
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = false })?.Dispose();
        else {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            if (File.Exists(path)) StorageRecent.Add(path);
        }
        DismissOnOutside();
    });

    void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            System.Text.Json.JsonException or System.Runtime.InteropServices.ExternalException) { Status(ex.Message); }
    }

    static void AddDock(DockPanel parent, UIElement child, Dock dock)
    {
        DockPanel.SetDock(child, dock);
        parent.Children.Add(child);
    }

    static void Section(Panel parent, string name)
    {
        var label = PanelControls.Eyebrow(name);
        label.Margin = new Thickness(6, 9, 0, 6);
        parent.Children.Add(label);
    }

    static Button Link(string glyph, string name, Action run, string? detail = null)
    {
        var content = new DockPanel();
        var icon = Text(glyph, 14, PanelControls.Secondary);
        icon.FontFamily = new FontFamily("Segoe Fluent Icons");
        icon.Margin = new Thickness(0, 0, 7, 0);
        AddDock(content, icon, Dock.Left);
        content.Children.Add(Text(name, 12));
        var button = PanelControls.Button(content, name, 30);
        if (detail is not null) button.ToolTip = PanelControls.Hint(detail);
        button.Click += (_, _) => run();
        return button;
    }

    static Button ActionButton(string caption, double height = 32)
    {
        var button = PanelControls.Button(caption, caption, height, true);
        button.FontSize = 12;
        button.Margin = new Thickness(2, 0, 0, 0);
        return button;
    }

    static Button Command(string caption, string hint, Action run, double height = 32)
    {
        var button = ActionButton(caption, height);
        System.Windows.Automation.AutomationProperties.SetName(button, hint);
        if (caption != hint) button.ToolTip = PanelControls.Hint(hint);
        button.Click += (_, _) => run();
        return button;
    }

    static TextBox Field(string label)
    {
        var box = new TextBox { Foreground = Brushes.White, CaretBrush = PanelControls.Silver, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = 13 };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        return box;
    }

    static Grid Placeholder(TextBox field, string hint)
    {
        var layers = new Grid();
        var label = Text(hint, 12, PanelControls.Secondary);
        label.IsHitTestVisible = false;
        field.TextChanged += (_, _) => label.Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        layers.Children.Add(label);
        layers.Children.Add(field);
        return layers;
    }

    static TextBlock Text(string text, double size, Brush? color = null, FontWeight? weight = null, bool wrap = false) => new() {
        Text = text, FontSize = size, Foreground = color ?? Brushes.White, FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis
    };
}
