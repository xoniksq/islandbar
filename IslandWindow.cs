using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace IslandBar;

sealed partial class IslandWindow : Window
{
    readonly bool _preview;
    readonly bool _inspect;
    readonly TrayFlyout _trayFlyout=new();
    readonly Button _trayButton;
    bool _trayOpening;
    readonly Border _frame;
    readonly Grid _surface = new();
    readonly Border _glint=new() {IsHitTestVisible=false,Opacity=0};
    readonly TranslateTransform _glintShift=new();
    readonly Button _closed;
    readonly TextBlock _smallClock;
    readonly StackPanel _dock = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _apps = new() { Orientation = Orientation.Horizontal };
    readonly TextBlock _language;
    readonly TextBlock _clock;
    readonly TextBlock _date;
    readonly Task<Catalog> _catalog = Task.Run(() => new Catalog());
    readonly Dictionary<string, AppTile> _tiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Motion _width = new(96), _height = new(32);
    readonly RectangleGeometry _clip = new();
    readonly TranslateTransform _dockOffset = new();
    readonly Native.MouseProc _mouseCallback;
    nint _mouseHook;
    Rect _hitRect;
    double _hitRadius;
    long _lastFrame;
    bool _rendering, _refreshing;
    readonly Dictionary<ContextMenu,nint> _openMenus=[];
    bool _escapeRegistered;
    readonly ScrollViewer _scroll;
    sealed class AppTile {
        public required DockApp App;
        public required Button Button;
        public required Border Indicator;
        public int IndicatorState=-1;
    }
    readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromMilliseconds(125) };
    readonly DispatcherTimer _outside = new() { Interval = TimeSpan.FromMilliseconds(20) };
    readonly EventWaitHandle _restore = new(false, EventResetMode.AutoReset, Program.RestoreEvent);
    readonly EventWaitHandle _expand = new(false, EventResetMode.AutoReset, Program.ExpandEvent);
    readonly EventWaitHandle _collapse = new(false, EventResetMode.AutoReset, Program.CollapseEvent);
    readonly EventWaitHandle _storage = new(false, EventResetMode.AutoReset, Program.StorageEvent);
    readonly RegisteredWaitHandle _restoreWait;
    readonly RegisteredWaitHandle _expandWait;
    readonly RegisteredWaitHandle _collapseWait;
    readonly RegisteredWaitHandle _storageWait;
    HwndSource? _source;
    nint _handle;
    bool _expanded, _fullscreen, _mouseDown, _closing;
    bool _expandAfterFullscreen;
    nint _fullscreenWindow;
    ShellKeys? _shellKeys;
    bool _restoreHotkey;
    int _ticks;
    double _openWidth = 600;
    ContextMenu _menu;
    StorageWindow? _storageWindow;
    string _clockKey = "";

    public IslandWindow(bool preview,bool inspect=false)
    {
        _preview = preview;
        _inspect = inspect;
        _mouseCallback = MouseHook;
        Title = "IslandBar — островок";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = preview || inspect;
        ShowActivated = false;
        Topmost = true;
        Width = Math.Max(320, SystemParameters.PrimaryScreenWidth-24);
        Height = 80;
        FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = false;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        var root = new Grid();
        _frame = new Border {
            Width=96, Height=32, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Bottom,
            Margin = new Thickness(12), Background = PanelControls.Dock,
            BorderBrush = PanelControls.Rim, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(28), Child = _surface,
            UseLayoutRounding=true,SnapsToDevicePixels=true,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .35, BlurRadius = 12, ShadowDepth = 3 }
        };
        root.Children.Add(_frame);
        Content = root;
        _glint.Background=new LinearGradientBrush(new GradientStopCollection {
            new(Colors.Transparent,.20),new(Color.FromArgb(27,240,244,255),.46),new(Colors.Transparent,.61)
        },new Point(0,0),new Point(1,.65)) {RelativeTransform=_glintShift};
        _surface.Children.Add(_glint);
        _surface.Clip = _clip;
        _surface.SizeChanged += (_, _) => {
            double w=Math.Max(0,_surface.ActualWidth),h=Math.Max(0,_surface.ActualHeight);
            _clip.Rect=new Rect(0,0,w,h);_clip.RadiusX=_clip.RadiusY=h/2;
        };
        _frame.SizeChanged += (_, _) => UpdateHitArea();

        _smallClock = Label("", 12, FontWeights.SemiBold);
        _closed = MakeButton(_smallClock, "Раскрыть панель задач", 94, 30);
        _closed.Background = Brushes.Transparent;
        _closed.Click += (_, _) => Expand();
        _surface.Children.Add(_closed);
        _dock.Margin = new Thickness(8, 0, 8, 0);
        _dock.HorizontalAlignment=HorizontalAlignment.Center;
        _dock.UseLayoutRounding=true;_dock.SnapsToDevicePixels=true;
        _dock.Opacity=0; _dock.IsHitTestVisible=false; _dock.RenderTransform=_dockOffset;
        _dock.Visibility=Visibility.Hidden;
        _surface.Children.Add(_dock);

        _startButton=CreateStartButton();
        _dock.Children.Add(_startButton);
        _scroll = new ScrollViewer {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _apps, MaxWidth = Math.Max(240, SystemParameters.PrimaryScreenWidth - 300)
        };
        _scroll.PreviewMouseWheel += (_, e) => { _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta*.4); e.Handled = true; };
        _dock.Children.Add(_scroll);
        InitializeFeatures();
        InitializeQueueButton();
        InitializeDockOverflow();
        _dock.Children.Add(new Border { Width = 1, Height = 24, Background = new SolidColorBrush(Color.FromArgb(38,255,255,255)), Margin = new Thickness(6,0,8,0), VerticalAlignment = VerticalAlignment.Center });

        var chevron=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 1 6 L 6 1 L 11 6"),Width=12,Height=8,Stroke=new SolidColorBrush(Color.FromRgb(226,228,233)),StrokeThickness=1.35,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
        _trayButton=MakeButton(chevron,"Показать скрытые значки",30);
        _featureTriggers["tray"]=_trayButton;
        _trayButton.Click+=async(_,_)=>await OpenTray();
        _dock.Children.Add(_trayButton);

        _language = Label("РУС", 11);
        var language = MakeButton(_language, "Переключить язык ввода", 34);
        language.Click += (_, _) => {
            nint target = Native.GetForegroundWindow();
            Native.PostMessage(target, 0x0050, 0, 1);
            Collapse();
        };
        _dock.Children.Add(language);
        var quick = MakeButton(Glyph("\uE767", 18), "Громкость и устройство вывода", 34);
        _featureTriggers["sound"]=quick;
        quick.Click += (_, _) => OpenSound();
        _dock.Children.Add(quick);
        var clocks = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _clock = Label("", 12); _date = Label("", 10);
        _date.Foreground = new SolidColorBrush(Color.FromRgb(193,195,202));
        clocks.Children.Add(_clock); clocks.Children.Add(_date);
        var clockButton = MakeButton(clocks, "Календарь и уведомления", 78);
        _featureTriggers["calendar"]=clockButton;
        clockButton.Click += (_, _) => OpenCalendar();
        _dock.Children.Add(clockButton);

        _menu = CreateMenu();
        TrackMenu(_menu);
        var preferencesMenu=new MenuItem {Header="Настройки островка"};
        preferencesMenu.Click+=(_,_)=>OpenSettings();
        _menu.Items.Add(preferencesMenu);
        var closeMenu = new MenuItem { Header = "Свернуть островок" };
        closeMenu.Click += (_, _) => Collapse();
        _menu.Opened+=(_,_)=>closeMenu.Visibility=_preferences.AlwaysExpanded?Visibility.Collapsed:Visibility.Visible;
        _menu.Items.Add(closeMenu);
        _menu.Items.Add(new Separator());
        var searchMenu=new MenuItem {Header="Поиск приложений · Ctrl+Alt+Пробел"};searchMenu.Click+=(_,_)=>OpenSearch();_menu.Items.Add(searchMenu);
        var storageMenu=new MenuItem {Header="Файлы · Хранилище"};storageMenu.Click+=(_,_)=>OpenStorage();_menu.Items.Add(storageMenu);
        var transferMenu=new MenuItem {Header="Очередь переносов"};transferMenu.Click+=(_,_)=>OpenTransferQueue();_menu.Items.Add(transferMenu);
        var layoutMenu=new MenuItem {Header="Раскладка окон"};layoutMenu.Click+=(_,_)=>{Expand();OpenLayouts();};_menu.Items.Add(layoutMenu);
        _menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Закрыть островок" };
        exit.Click += (_, _) => Close();
        _menu.Items.Add(exit);
        _frame.ContextMenu = _menu;

        _restoreWait = ThreadPool.RegisterWaitForSingleObject(_restore, (_, _) => Dispatcher.BeginInvoke(() => Close()), null, -1, false);
        _expandWait = ThreadPool.RegisterWaitForSingleObject(_expand, (_, _) => Dispatcher.BeginInvoke(() => Expand()), null, -1, false);
        _collapseWait = ThreadPool.RegisterWaitForSingleObject(_collapse, (_, _) => Dispatcher.BeginInvoke(() => Collapse()), null, -1, false);
        _storageWait = ThreadPool.RegisterWaitForSingleObject(_storage, (_, _) => Dispatcher.BeginInvoke(() => {
            if (_closing || UpdateFullscreenVisibility()) return;
            if (_storageWindow is { } window) window.Activate();
            else OpenStorage();
        }), null, -1, false);
        SourceInitialized += (_, _) => InitializeNative();
        LocationChanged += (_, _) => UpdateHitArea();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Position));
        Loaded += (_, _) => {
            UpdateClock(); Position();
            if (!_preview) Native.SetTaskbarVisible(false);
            ApplyPreferences();
            _heartbeat.Start();
            if (!_preview) _fullscreenTimer.Start();
            WriteStatus();
            _ = RefreshAppsAsync();
            _ = RefreshMusicAsync();
            ExtrasTick();
        };
        _heartbeat.Tick += (_, _) => Heartbeat();
        _fullscreenTimer.Tick += (_, _) => UpdateFullscreenVisibility();
        _outside.Tick += (_, _) => OutsideTick();
        Closed += (_, _) => Cleanup();
        Closing += (_, e) => { if (_storageWindow?.TransferRunning == true || _storageQueue?.Active is not null) e.Cancel = true; };
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    static TextBlock Label(string text, double size, FontWeight? weight = null) => new() {
        Text = text, FontSize = size, Foreground = Brushes.White, FontWeight = weight ?? FontWeights.Normal,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center
    };
    static TextBlock Glyph(string text, double size) { var label = Label(text, size); label.FontFamily = new FontFamily("Segoe Fluent Icons"); return label; }
    static Grid WindowsLogo()
    {
        var grid = new Grid { Width = 21, Height = 21 };
        for (int i=0;i<2;i++) { grid.RowDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); }
        for (int i=0;i<4;i++) {
            var square = new Border { Background = PanelControls.Silver, Margin = new Thickness(1),CornerRadius=new CornerRadius(1.25) };
            Grid.SetRow(square, i/2); Grid.SetColumn(square, i%2); grid.Children.Add(square);
        }
        return grid;
    }
    static Button MakeButton(object content, string name, double width, double height = 42)
    {
        var fill=new SolidColorBrush(Colors.Transparent);
        var scale=new ScaleTransform(1,1);
        var shift=new TranslateTransform();
        var transforms=new TransformGroup();transforms.Children.Add(scale);transforms.Children.Add(shift);
        var button = new Button { Content = content, Width = width, Height = height, BorderThickness = new Thickness(0),
            Background = fill, Foreground = Brushes.White, ToolTip = PanelControls.Hint(name), Focusable = false,
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0), Cursor = Cursors.Hand, RenderTransformOrigin=new Point(.5,.5), RenderTransform=transforms };
        ToolTipService.SetInitialShowDelay(button,650);
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var layers=new FrameworkElementFactory(typeof(Grid));
        layers.AppendChild(border);
        var marker=new FrameworkElementFactory(typeof(Border),"OpenMarker");
        marker.SetValue(FrameworkElement.WidthProperty,11d);
        marker.SetValue(FrameworkElement.HeightProperty,2d);
        marker.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
        marker.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Bottom);
        marker.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,0,1));
        marker.SetValue(Border.CornerRadiusProperty,new CornerRadius(1));
        marker.SetValue(Border.BackgroundProperty,PanelControls.Silver);
        marker.SetValue(UIElement.OpacityProperty,0d);
        marker.SetValue(UIElement.IsHitTestVisibleProperty,false);
        layers.AppendChild(marker);
        var template=new ControlTemplate(typeof(Button)){VisualTree=layers};
        var open=new Trigger {Property=TagProperty,Value="open"};
        open.Setters.Add(new Setter(UIElement.OpacityProperty,.92d,"OpenMarker"));
        template.Triggers.Add(open);
        button.Template=template;
        void Move(double zoom,double y,byte alpha) {
            var duration=TimeSpan.FromMilliseconds(ChromeMotion.Enabled?150:0);
            var easing=new CubicEase {EasingMode=EasingMode.EaseOut};
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,new DoubleAnimation(ChromeMotion.Enabled?zoom:1,duration){EasingFunction=easing});
            scale.BeginAnimation(ScaleTransform.ScaleYProperty,new DoubleAnimation(ChromeMotion.Enabled?zoom:1,duration){EasingFunction=easing});
            shift.BeginAnimation(TranslateTransform.YProperty,new DoubleAnimation(ChromeMotion.Enabled?y:0,duration){EasingFunction=easing});
            fill.BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation(Color.FromArgb(alpha,255,255,255),duration));
        }
        button.MouseEnter += (_, _) => Move(1.035,-1.7,23);
        button.MouseLeave += (_, _) => Move(1,0,0);
        button.PreviewMouseLeftButtonDown += (_, _) => Move(.955,.7,34);
        button.PreviewMouseLeftButtonUp += (_, _) => Move(1.035,-1.7,23);
        return button;
    }

    void UpdatePanelMarker()
    {
        string kind=_featureKind=="mixer"?"sound":_featureKind;
        foreach(var pair in _featureTriggers)pair.Value.Tag=pair.Key==kind||pair.Key=="storage"&&_storageWindow is not null?"open":null;
        _startButton.Tag=_startMenu is {Dismissing:false}?"open":null;
    }
    internal static ContextMenu CreateMenu()
    {
        var background=PanelControls.Shell;
        var menu=new ContextMenu {Background=background,Foreground=Brushes.White};
        var shell=new FrameworkElementFactory(typeof(Border));shell.SetValue(Border.BackgroundProperty,background);
        shell.SetValue(Border.CornerRadiusProperty,new CornerRadius(15));shell.SetValue(Border.PaddingProperty,new Thickness(6));
        shell.SetValue(Border.BorderBrushProperty,PanelControls.Rim);shell.SetValue(Border.BorderThicknessProperty,new Thickness(1));
        shell.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        menu.Template=new ControlTemplate(typeof(ContextMenu)){VisualTree=shell};
        var style=new Style(typeof(MenuItem));style.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.White));
        var row=new FrameworkElementFactory(typeof(Border),"Row");row.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));row.SetValue(Border.PaddingProperty,new Thickness(8,7,12,7));
        row.SetValue(Border.BackgroundProperty,Brushes.Transparent);
        var dock=new FrameworkElementFactory(typeof(DockPanel));
        var check=new FrameworkElementFactory(typeof(TextBlock),"Check");check.SetValue(TextBlock.TextProperty,"✓");check.SetValue(FrameworkElement.WidthProperty,20d);check.SetValue(UIElement.VisibilityProperty,Visibility.Hidden);
        dock.AppendChild(check);
        var header=new FrameworkElementFactory(typeof(ContentPresenter));header.SetValue(ContentPresenter.ContentSourceProperty,"Header");header.SetValue(ContentPresenter.RecognizesAccessKeyProperty,true);dock.AppendChild(header);row.AppendChild(dock);
        var template=new ControlTemplate(typeof(MenuItem)){VisualTree=row};
        var highlight=new Trigger {Property=MenuItem.IsHighlightedProperty,Value=true};highlight.Setters.Add(new Setter(Border.BackgroundProperty,new SolidColorBrush(Color.FromArgb(26,255,255,255)),"Row"));
        var selected=new Trigger {Property=MenuItem.IsCheckedProperty,Value=true};selected.Setters.Add(new Setter(UIElement.VisibilityProperty,Visibility.Visible,"Check"));
        var disabled=new Trigger {Property=MenuItem.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.45,"Row"));
        template.Triggers.Add(highlight);template.Triggers.Add(selected);template.Triggers.Add(disabled);style.Setters.Add(new Setter(Control.TemplateProperty,template));menu.Resources[typeof(MenuItem)]=style;
        var separator=new Style(typeof(Separator));
        var line=new FrameworkElementFactory(typeof(Border));line.SetValue(Border.BackgroundProperty,new SolidColorBrush(Color.FromArgb(36,255,255,255)));line.SetValue(FrameworkElement.HeightProperty,1d);line.SetValue(FrameworkElement.MarginProperty,new Thickness(8,4,8,4));
        separator.Setters.Add(new Setter(Control.TemplateProperty,new ControlTemplate(typeof(Separator)){VisualTree=line}));menu.Resources[typeof(Separator)]=separator;
        return menu;
    }

    void InitializeNative()
    {
        _handle = new WindowInteropHelper(this).Handle;
        if (!_preview && !_inspect) Native.SetWindowLong(_handle, -20, Native.GetWindowLong(_handle, -20) | 0x08000080);
        _source = HwndSource.FromHwnd(_handle);
        _source.AddHook(Hook);
        _restoreHotkey = Native.RegisterHotKey(_handle, 3, 0x4007, 0x42);
        if (!_restoreHotkey) Program.Log("Ctrl+Alt+Shift+B is in use");
        _searchHotkey=Native.RegisterHotKey(_handle,4,0x4003,0x20);
        if(!_searchHotkey)Program.Log("Ctrl+Alt+Space is in use");
    }
    nint Hook(nint window, int message, nint wparam, nint lparam, ref bool handled)
    {
        if(message==0x0021&&!_inspect&&!_preview)return KeepForeground(window,message,wparam,lparam,ref handled);
        if (message == 0x0084) { // WM_NCHITTEST carries the screen point being tested.
            long point = lparam.ToInt64();
            if (!InsideCapsule(unchecked((short)point), unchecked((short)(point >> 16)))) {
                handled = true;
                return -1;
            }
        }
        if (message == 0x007E) Dispatcher.BeginInvoke(new Action(Position)); // WM_DISPLAYCHANGE
        if (message == 0x0312)
        {
            switch ((int)wparam) {
                case 2: if(_storageWindow is not null)_storageWindow.HandleEscape();else if(_startMenu is not null)CloseStartMenu();else Collapse();break;
                case 3: Close(); break;
                case 4: OpenSearch(); break;
            }
            handled = true;
        }
        return 0;
    }
    static nint KeepForeground(nint window,int message,nint wparam,nint lparam,ref bool handled)
    {
        if(message!=0x0021)return 0;
        handled=true;
        return 3; // MA_NOACTIVATE: let the click reach the control and keep the foreground window.
    }

    async Task RefreshAppsAsync()
    {
        if(_refreshing||_closing)return;_refreshing=true;
        try {
            var catalog=await _catalog;
            var apps=await Task.Run(catalog.GetApps);
            if(_closing)return;
            var keys=apps.Select(a=>a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var key in _tiles.Keys.Where(k=>!keys.Contains(k)).ToArray()){_apps.Children.Remove(_tiles[key].Button);_tiles.Remove(key);}
            for(int i=0;i<apps.Count;i++) {
                var app=apps[i];
                if(!_tiles.TryGetValue(app.Key,out var tile)){tile=MakeTile(app);_tiles[app.Key]=tile;}
                tile.App=app;
                int index=_apps.Children.IndexOf(tile.Button);
                if(index!=i){if(index>=0)_apps.Children.RemoveAt(index);_apps.Children.Insert(i,tile.Button);}
                UpdateTile(tile);
            }
            double previous=_openWidth;MeasureDock();UpdateIndicators();
            if(_expanded&&Math.Abs(previous-_openWidth)>.1)BeginMotion();
            WriteStatus();
        }catch(Exception ex){Program.Log("catalog: "+ex);}
        finally{_refreshing=false;}
    }
    AppTile MakeTile(DockApp app)
    {
        var visual=new Grid {Width=42,Height=44};
        if(app.Key.Contains("OpenAI.Codex",StringComparison.OrdinalIgnoreCase))visual.Children.Add(new Border {Width=30,Height=30,CornerRadius=new CornerRadius(7),Background=new SolidColorBrush(Color.FromRgb(224,225,230))});
        if(app.Icon is not null){var image=new Image {Source=app.Icon,Width=28,Height=28,UseLayoutRounding=true,SnapsToDevicePixels=true};RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);visual.Children.Add(image);}
        else visual.Children.Add(Glyph("\uE737",22));
        var indicator=new Border {Width=4,Height=3,Opacity=0,CornerRadius=new CornerRadius(2),Background=new SolidColorBrush(Color.FromRgb(125,127,137)),VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,1)};
        visual.Children.Add(indicator);
        var button=MakeButton(visual,app.Name,42,44);
        var tile=new AppTile {App=app,Button=button,Indicator=indicator};
        ConfigureIconOrdering(tile);button.MouseEnter+=(_,_)=>HoverPreview(tile);
        button.Click+=(_,_)=>ActivateApp(tile.App);
        var menu=CreateMenu();
        ContextMenuService.SetPlacement(button,PlacementMode.Top);
        ContextMenuService.SetVerticalOffset(button,-10);
        TrackMenu(menu);button.ContextMenu=menu;
        PopulateAppMenu(tile,menu);
        button.ContextMenuOpening+=(_,_)=>{ClosePreview();PopulateAppMenu(tile,menu);};
        return tile;
    }
    void UpdateTile(AppTile tile)
    {
        if(tile.Button.ToolTip is ToolTip tip)tip.Content=tile.App.Name;
        System.Windows.Automation.AutomationProperties.SetName(tile.Button,tile.App.Name);
    }
    void PopulateAppMenu(AppTile tile,ContextMenu menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new MenuItem {Header=tile.App.Name,IsEnabled=false});
        menu.Items.Add(new Separator());
        void Add(string label,Action action,bool collapse=true){
            var item=new MenuItem {Header=label};
            item.Click+=(_,_)=>{try{if(collapse)Collapse();else CloseMenus();action();}catch(Exception ex){Program.Log("app menu: "+ex.Message);}};
            menu.Items.Add(item);
        }
        var windows=tile.App.Windows.Where(Native.IsWindow).ToArray();
        foreach(var window in windows){
            string label=windows.Length==1?"Перейти к окну":Native.Title(window);
            Add(label,()=>Native.Activate(window));
        }
        if(tile.App.LaunchPath is not null)Add(windows.Length==0?"Открыть":"Запустить ещё раз",()=>Catalog.Launch(tile.App));
        if(tile.App.LaunchPath is not null){menu.Items.Add(new Separator());Add(tile.App.Pinned?"Открепить от островка":"Закрепить в островке",()=>{DockSettings.Toggle(tile.App);_=RefreshAppsAsync();},false);}
        Add("Переместить левее",()=>MoveIcon(tile,-1),false);Add("Переместить правее",()=>MoveIcon(tile,1),false);
        if(windows.Length>0){
            menu.Items.Add(new Separator());
            Add(windows.Length==1?"Свернуть окно":"Свернуть все окна",()=>{foreach(var window in windows)if(Native.IsWindow(window))Native.ShowWindow(window,6);});
            Add(windows.Length==1?"Закрыть окно":"Закрыть все окна",()=>{foreach(var window in windows)if(Native.IsWindow(window))Native.PostMessage(window,0x0010,0,0);});
        }
    }
    void UpdateIndicators()
    {
        nint active=Native.GetForegroundWindow();
        foreach(var tile in _tiles.Values){
            int state=tile.App.Windows.Count==0?0:tile.App.Windows.Contains(active)?2:1;
            if(state==tile.IndicatorState)continue;tile.IndicatorState=state;
            var duration=TimeSpan.FromMilliseconds(ChromeMotion.Enabled?150:0);
            tile.Indicator.BeginAnimation(OpacityProperty,new DoubleAnimation(state==0?0:1,duration));
            tile.Indicator.BeginAnimation(FrameworkElement.WidthProperty,new DoubleAnimation(state==2?15:4,duration));
            ((SolidColorBrush)tile.Indicator.Background).BeginAnimation(SolidColorBrush.ColorProperty,new ColorAnimation(state==2?Color.FromRgb(224,226,234):Color.FromRgb(125,127,137),duration));
        }
    }
    void ActivateApp(DockApp app)
    {
        try{
            var windows=app.Windows.Where(Native.IsWindow).ToList();nint active=Native.GetForegroundWindow();bool launchNew=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            int index=windows.IndexOf(active);
            Collapse();
            if(windows.Count==0||launchNew)Catalog.Launch(app);
            else if(index>=0&&_preferences.ClickToMinimize)Native.ShowWindow(active,6);
            else if(index<0||windows.Count>1)Native.Activate(windows[(index+1)%windows.Count]);
            Program.Log("app: "+app.Name);
        }catch(Exception ex){Program.Log("launch: "+ex.Message);}
    }

    void RunShellAction(ShellAction action)
    {
        Position();
        if(_closing||UpdateFullscreenVisibility())return;
        Program.Log("shell shortcut: "+action);
        if(action==ShellAction.Start){ToggleStartMenu();return;}
        if(!_expanded)Expand();
        switch(action){
            case ShellAction.Calendar:OpenCalendar();break;
            case ShellAction.Sound:OpenSound();break;
            case ShellAction.Tray:_=OpenTray();break;
        }
    }
    void MeasureDock()
    {
        FitDockModules();
        var available=new Size(double.PositiveInfinity,58);
        double fixedWidth=0;
        foreach(UIElement child in _dock.Children){
            if(ReferenceEquals(child,_scroll))continue;
            child.Measure(available);
            fixedWidth+=child.DesiredSize.Width;
        }
        _scroll.MaxWidth=Math.Max(0,Width-26-_dock.Margin.Left-_dock.Margin.Right-fixedWidth);
        _dock.Measure(available);
        double dpi=VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pixels=(int)Math.Ceiling(Math.Max(200,_dock.DesiredSize.Width+2)*dpi);pixels+=pixels&1;
        _openWidth = Math.Min(Width - 24, pixels/dpi);
    }

    public void Expand()
    {
        if (_closing || UpdateFullscreenVisibility()) return;
        ExpandDock();
    }
    void ExpandDock()
    {
        ClosePreview();
        if (!IsVisible) Show();
        if (!_preview) Native.SetTaskbarVisible(false);
        KeepDockOnTop(raise: true);
        if (_expanded) return;
        _expanded = true;
        if(_preferences.WindowPreviews)_previewTimer.Start();
        _toastUntil=DateTime.MinValue;
        MeasureDock(); BeginMotion();
        SyncOutsideTracking();
        _ = RefreshAppsAsync();
        Program.Log("expanded"); WriteStatus();
    }
    void Collapse(bool force=false)
    {
        if (_closing) return;
        ClosePreview();
        CloseStartMenu();
        bool hadToast=ToastVisible;_toastUntil=DateTime.MinValue;
        CloseFeature();
        if (!_fullscreen) _storageWindow?.DismissOnOutside();
        CloseMenus();
        if(_preferences.AlwaysExpanded&&!force){SyncOutsideTracking();return;}
        if(!_expanded){if(hadToast)BeginMotion();return;}
        _expanded = false;
        SyncOutsideTracking();
        _dock.IsHitTestVisible=false;
        foreach(var button in _dock.Children.OfType<Button>().Concat(_tiles.Values.Select(t=>t.Button)))if(button.ToolTip is ToolTip tip)tip.IsOpen=false;
        BeginMotion();
        Program.Log("collapsed"); WriteStatus();
    }
    void BeginMotion()
    {
        _frame.UseLayoutRounding=false;
        if(_toastButton is not null)_toastButton.Visibility=ToastVisible?Visibility.Visible:Visibility.Hidden;
        ToolTipService.SetIsEnabled(_closed,false);
        if(_closed.ToolTip is ToolTip clockTip)clockTip.IsOpen=false;
        foreach(var button in _dock.Children.OfType<Button>().Concat(_tiles.Values.Select(t=>t.Button)))ToolTipService.SetIsEnabled(button,false);
        if(_expanded)_dock.Visibility=Visibility.Visible;else _closed.Visibility=Visibility.Visible;
        if(!ChromeMotion.Enabled){
            if(_rendering){CompositionTarget.Rendering-=RenderFrame;_rendering=false;}
            _width.Snap(TargetWidth);_height.Snap(TargetHeight);ApplyMotion();IdleVisibility();WriteStatus();
            return;
        }
        if(_rendering)return;_rendering=true;_lastFrame=Stopwatch.GetTimestamp();CompositionTarget.Rendering+=RenderFrame;
    }
    void RenderFrame(object? sender, EventArgs args)
    {
        long now=Stopwatch.GetTimestamp();double dt=Math.Clamp((now-_lastFrame)/(double)Stopwatch.Frequency,.0001,.1);_lastFrame=now;
        double w=TargetWidth,h=TargetHeight;
        _width.Step(w,dt,_expanded?20:24);_height.Step(h,dt,_expanded?20:24);ApplyMotion();
        if(_checking)_samples.Add(new(now/(double)Stopwatch.Frequency,_frame.Width,_frame.Height,Left,Top,Width,Height));
        if(_width.AtRest(w)&&_height.AtRest(h)){_rendering=false;CompositionTarget.Rendering-=RenderFrame;IdleVisibility();WriteStatus();}
    }
    void IdleVisibility(){
        _frame.UseLayoutRounding=true;
        _dock.Visibility=_expanded?Visibility.Visible:Visibility.Hidden;_closed.Visibility=_expanded||ToastVisible?Visibility.Hidden:Visibility.Visible;
        ToolTipService.SetIsEnabled(_closed,!_expanded);
        foreach(var button in _dock.Children.OfType<Button>().Concat(_tiles.Values.Select(t=>t.Button)))ToolTipService.SetIsEnabled(button,_expanded);
        foreach(var tile in _tiles.Values)ToolTipService.SetIsEnabled(tile.Button,_expanded&&tile.App.Windows.Count==0);
    }
    static double Smooth(double from,double to,double value){double t=Math.Clamp((value-from)/(to-from),0,1);return t*t*(3-2*t);}
    void ApplyMotion()
    {
        _frame.Width=Math.Clamp(_width.Value,96,Width-24);_frame.Height=Math.Clamp(_height.Value,32,56);
        double p=Math.Clamp((_frame.Width-96)/Math.Max(1,_openWidth-96),0,1);
        _glintShift.X=p*1.10-.53;
        _glint.Opacity=ChromeMotion.Enabled&&_rendering?Math.Sin(p*Math.PI):0;
        _dock.Opacity=Smooth(.18,.82,p);_dockOffset.Y=(1-_dock.Opacity)*5;
        _closed.Opacity=1-Smooth(.015,.18,p);
        if(_toastButton is not null){_toastButton.Opacity=ToastVisible?Smooth(.1,.8,(_frame.Width-96)/192):0;_toastButton.IsHitTestVisible=ToastVisible;}
        _dock.IsHitTestVisible=_expanded&&p>.88;_closed.IsHitTestVisible=!_expanded&&!ToastVisible&&p<.25;
    }
    void Position()
    {
        if (_handle == 0 || _closing) return;
        var current = Native.Display(_handle);
        var display = _preview ? current : Native.PrimaryDisplay();
        if (display.Id == 0) return;
        var bounds = _preview ? display.Work : display.Bounds;
        if (current.Id != display.Id)
            Native.SetWindowPos(_handle, 0, bounds.Left + 12, bounds.Bottom - 120, 320, 100, 0x14);
        double scale = Math.Max(1, Native.GetDpiForWindow(_handle)) / 96d;
        Width = (bounds.Right - bounds.Left) / scale - 24;
        Height = 80;
        Native.SetWindowPos(_handle, 0, bounds.Left + (int)(12 * scale), bounds.Bottom - (int)(Height * scale),
            (int)(Width * scale), (int)(Height * scale), 0x14);
        double previous = _openWidth;
        MeasureDock();
        if (_expanded && Math.Abs(previous - _openWidth) > .5) BeginMotion();
        UpdateHitArea();
    }

    void KeepDockOnTop(bool raise = false)
    {
        if (_preview || _fullscreen || _handle == 0 || !IsVisible || _trayOpening) return;
        if (!raise) {
            if (!_expanded || _hitRect.IsEmpty || _openMenus.Count != 0 || _featurePopup.IsOpen ||
                _startMenu is not null || _storageWindow is not null || _windowPreview is not null) return;
            int x = (int)(_hitRect.Left + _hitRect.Width / 2);
            int y = (int)(_hitRect.Top + 5);
            bool Covered(int px) => Native.IsApplicationWindow(Native.WindowFromPoint(new Native.Point { X = px, Y = y }));
            if (!Covered(x) && !Covered(x - (int)(_hitRect.Width / 4)) && !Covered(x + (int)(_hitRect.Width / 4))) return;
        }
        const uint keepSizePositionAndFocus = 0x0001 | 0x0002 | 0x0010;
        Native.SetWindowPos(_handle, -1, 0, 0, 0, 0, keepSizePositionAndFocus);
    }

    Rect DockDisplayArea()
    {
        var display = Native.Display(_handle);
        if (display.Id == 0) return SystemParameters.WorkArea;
        double scale = Math.Max(1, Native.GetDpiForWindow(_handle)) / 96d;
        return new Rect(display.Bounds.Left / scale, display.Bounds.Top / scale,
            (display.Bounds.Right - display.Bounds.Left) / scale, (display.Bounds.Bottom - display.Bounds.Top) / scale);
    }
    void UpdateHitArea()
    {
        if(!IsLoaded)return;
        var top=_frame.PointToScreen(new Point(0,0));var bottom=_frame.PointToScreen(new Point(_frame.ActualWidth,_frame.ActualHeight));
        _hitRect=new Rect(top,bottom);_hitRadius=_hitRect.Height/2;
    }
    bool InsideCapsule(double x,double y)
    {
        if(!_hitRect.Contains(x,y))return false;
        double cx=Math.Clamp(x,_hitRect.Left+_hitRadius,_hitRect.Right-_hitRadius),cy=_hitRect.Top+_hitRadius;
        return (x-cx)*(x-cx)+(y-cy)*(y-cy)<=_hitRadius*_hitRadius;
    }
    nint MouseHook(int code,nint message,nint data)
    {
        if(code>=0&&message is 0x0201 or 0x0204){
            var p=Marshal.PtrToStructure<Native.MouseData>(data).Position;
            OutsideClick(p.X,p.Y);
        }
        return Native.CallNextHookEx(_mouseHook,code,message,data);
    }
    void SyncOutsideTracking()
    {
        bool needed=!_closing&&!_fullscreen&&((_expanded&&!_preferences.AlwaysExpanded)||_openMenus.Count>0||_featurePopup.IsOpen||_startMenu is not null||_storageWindow is not null);
        if(needed){
            if(_mouseHook==0&&!_outside.IsEnabled){
                _mouseDown=Native.GetAsyncKeyState(1)<0||Native.GetAsyncKeyState(2)<0;
                _mouseHook=Native.SetWindowsHookEx(14,_mouseCallback,Native.GetModuleHandle(null),0);
                if(_mouseHook==0)_outside.Start();
            }
        }else{
            _outside.Stop();
            if(_mouseHook!=0){Native.UnhookWindowsHookEx(_mouseHook);_mouseHook=0;}
        }
        bool takeEscape=needed&&_storageWindow is null&&!_trayContextOpening&&!NativeTrayMenuOpen;
        if(takeEscape&&!_escapeRegistered)_escapeRegistered=Native.RegisterHotKey(_handle,2,0x4000,0x1B);
        if(!takeEscape&&_escapeRegistered){Native.UnregisterHotKey(_handle,2);_escapeRegistered=false;}
    }
    void CloseMenus(){foreach(var menu in _openMenus.Keys.ToArray())menu.IsOpen=false;}
    void OutsideClick(int x,int y)
    {
        if (_startMenu?.ConfirmingPower == true) return;
        if (_storageWindow is { } storage && storage.ContainsScreenPoint(x,y)) return;
        if(InsideFeature(x,y)||InsidePreview(x,y)||InsideStartMenu(x,y)||InsideNativeTrayMenu(x,y))return;
        foreach(var window in _openMenus.Values)
            if(window!=0&&Native.GetWindowRect(window,out var r)&&x>=r.Left&&x<r.Right&&y>=r.Top&&y<r.Bottom)return;
        bool inside=InsideCapsule(x,y);
        bool trigger=InsideFeatureTrigger(x,y);
        var startToClose=_startMenu is not null&&!PointInside(_startButton,x,y)?_startMenu:null;
        var storageToClose=_storageWindow is { } openStorage&&
            (!_featureTriggers.TryGetValue("storage",out var storageTrigger)||!PointInside(storageTrigger,x,y))?openStorage:null;
        if(_openMenus.Count>0||_featurePopup.IsOpen||!inside||startToClose is not null||storageToClose is not null)
            Dispatcher.BeginInvoke(()=>{
                if (_startMenu?.ConfirmingPower == true) return;
                CloseMenus();if(!trigger)CloseFeature();
                startToClose?.Dismiss();
                storageToClose?.DismissOnOutside();
                if(!inside)Collapse();
            },DispatcherPriority.Input);
    }
    void TrackMenu(ContextMenu menu)
    {
        menu.Opened+=(_,_)=>{
            _openMenus[menu]=(PresentationSource.FromVisual(menu) as HwndSource)?.Handle??0;
            ChromeMotion.Enter(menu,anchored:false);
            SyncOutsideTracking();WriteStatus();
        };
        menu.Closed+=(_,_)=>{
            _openMenus.Remove(menu);SyncOutsideTracking();WriteStatus();
        };
    }
    void UpdateClock()
    {
        string key = DateTime.Now.ToString("yyyyMMddHHmm");
        if (key != _clockKey) {
            _clockKey = key;
            _clock.Text = DateTime.Now.ToString("HH:mm");
            UpdateQueueIndicator();
            _date.Text = DateTime.Now.ToString("dd.MM.yyyy");
            if(_closed.ToolTip is ToolTip tip)tip.Content = DateTime.Now.ToString("D", CultureInfo.GetCultureInfo("ru-RU")) + " · нажми, чтобы раскрыть";
        }
        string language = Native.Language();
        _language.Text = language == "RU" ? "РУС" : language;
    }
    void OutsideTick()
    {
        bool down = Native.GetAsyncKeyState(1) < 0 || Native.GetAsyncKeyState(2) < 0;
        if (down && !_mouseDown && Native.GetCursorPos(out var point))
        {
            OutsideClick(point.X,point.Y);
        }
        _mouseDown = down;
    }
    bool UpdateFullscreenVisibility()
    {
        if (_closing || _preview || _handle == 0) return false;
        bool full = _preferences.HideFullscreen && Native.FullscreenBusy(_handle, ref _fullscreenWindow);
        if (full == _fullscreen) return full;
        _fullscreen = full;
        if (full) {
            _expandAfterFullscreen = _expanded;
            StopFileDrag(false);
            CloseFeature(immediate:true);
            CloseStartMenu(immediate:true);
            _searchWindow?.Dismiss(immediate:true);
            _storageWindow?.Hide();
            _setEditor?.Hide();
            Collapse(force:true);
            Hide();
        } else {
            Show();
            if (_expandAfterFullscreen || _preferences.AlwaysExpanded) ExpandDock();
            ShowWithoutActivation(_storageWindow);
            ShowWithoutActivation(_setEditor);
        }
        SyncOutsideTracking();
        WriteStatus();
        return full;
    }

    static void ShowWithoutActivation(Window? window)
    {
        if (window is null || window.IsVisible) return;
        bool activate = window.ShowActivated;
        window.ShowActivated = false;
        window.Show();
        window.ShowActivated = activate;
    }

    void Heartbeat()
    {
        try {
            UpdateClock();
            FeatureTick();
            TickUpdates();
            if (_closing) return;
            KeepDockOnTop();
            if (!_preview && !_trayOpening) Native.SetTaskbarVisible(false);
            if (++_ticks % 2 == 0) _ = RefreshAppsAsync();
            if (_expanded) UpdateIndicators();
        } catch (Exception ex) { Program.Log("timer: " + ex.Message); }
    }
    void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) {
        if(args.PropertyName is "WorkArea" or "PrimaryScreenWidth" or "PrimaryScreenHeight")
            Dispatcher.BeginInvoke(()=>{CloseStartMenu();Position();});
    }
    void WriteStatus()
    {
        try { File.WriteAllText(Path.Combine(Program.Folder,"status.json"), System.Text.Json.JsonSerializer.Serialize(new {
            Version=typeof(IslandWindow).Assembly.GetName().Version?.ToString(3), Pid = Environment.ProcessId, Preview = _preview, Inspect=_inspect, Expanded = _expanded,
            TaskbarVisible = Native.IsTaskbarVisible, Applications = _apps.Children.Count,
            Packaged = Native.IsPackaged, Fullscreen = _fullscreen, IslandVisible = IsVisible, AlwaysExpanded=_preferences.AlwaysExpanded,
            AnimationsEnabled=ChromeMotion.Enabled, EscapeHotkeyRegistered=_escapeRegistered, UpdatePending=_pendingUpdate,
            ReturnShortcutAvailable = File.Exists(Startup.ReturnShortcut), RestoreHotkeyRegistered = _restoreHotkey,
            SearchHotkeyRegistered=_searchHotkey,ShellKeysInstalled=_shellKeys?.Installed==true,StartMenuOpen=_startMenu is not null,FeaturePanel=_featureKind,StorageOpen=_storageWindow is not null,MusicTitle=_musicService.Current?.Title,MusicPlaying=_musicService.Current?.Playing,TrackToast=ToastVisible,
            NetworkDown=_networkRate?.Down,NetworkUp=_networkRate?.Up,PreviewThumbnails=_windowPreview?.ThumbnailCount??0,
            CpuLoad=_pcState?.CpuLoad,CpuTemperature=_pcState?.CpuTemperature,GpuLoad=_pcState?.GpuLoad,GpuTemperature=_pcState?.GpuTemperature,ActiveDownloads=_downloadMonitor.Current.Count(d=>d.Active),DownloadSources=_downloadMonitor.SourcesAvailable,FileDragging=_fileDragging,DragPreviews=_dragActivationCount,
            Animating=_rendering,CapsuleWidth=_frame.Width,CapsuleHeight=_frame.Height,ViewportWidth=Width,ViewportHeight=Height,MouseHookActive=_mouseHook!=0,TrayOpen=_featureKind=="tray"&&_featurePopup.IsOpen,NativeTrayVisible=Native.IsWindowVisible(Native.TrayOverflowWindow()),NativeTrayMenuOpen,OpenMenus=_openMenus.Count
        })); } catch { }
    }
    void Cleanup()
    {
        _closing = true;
        _trayMenuTimer?.Stop();
        _shellKeys?.Dispose();
        _previewTimer.Stop();ClosePreview();_setEditor?.Close();
        StopFileDrag(false);
        CloseFeature(immediate:true);CloseStartMenu(immediate:true);_searchWindow?.Dismiss(immediate:true);
        _storageWindow?.Close();
        _heartbeat.Stop(); _fullscreenTimer.Stop(); _outside.Stop();
        if(_mouseHook!=0)Native.UnhookWindowsHookEx(_mouseHook);
        if(_rendering)CompositionTarget.Rendering-=RenderFrame;
        _restoreWait.Unregister(null); _expandWait.Unregister(null);
        _collapseWait.Unregister(null);
        _storageWait.Unregister(null);
        _restore.Dispose(); _expand.Dispose(); _collapse.Dispose(); _storage.Dispose();
        for (int id=1;id<=4;id++) Native.UnregisterHotKey(_handle,id);
        _source?.RemoveHook(Hook);
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        Program.Log("exit");
    }
}
