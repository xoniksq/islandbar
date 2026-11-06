using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace IslandBar;

sealed record LaunchEntry(string Name,string Path,bool Pinned=false,DockApp? Favorite=null, Action? Run=null);

sealed record StartMenuPage(
    Rect Bounds,
    UIElement Favorites,
    UIElement Shortcuts,
    IReadOnlyList<DockApp> Pinned,
    Action AfterLaunch,
    Action OpenSettings);

sealed class SearchWindow:Window
{
    readonly TextBox _query=new() {FontSize=17,Foreground=Brushes.White,Background=Brushes.Transparent,
        BorderThickness=new Thickness(0),CaretBrush=PanelControls.Silver,SelectionBrush=PanelControls.Silver,
        SelectionOpacity=.35,Padding=new Thickness(0),VerticalContentAlignment=VerticalAlignment.Center};
    readonly ListBox _results=new() {Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=Brushes.White,HorizontalContentAlignment=HorizontalAlignment.Stretch};
    readonly TextBlock _hint=new() {Text="Загружаю приложения…",Foreground=PanelControls.Secondary,FontSize=11,Margin=new Thickness(0,10,0,0)};
    readonly Task<List<LaunchEntry>> _index=Task.Run(BuildIndex);
    readonly StartMenuPage? _start;
    readonly LaunchEntry[] _actions;
    readonly Border _frame;
    readonly StackPanel _keyHints=new() {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
    int _revision;
    bool _closing,_dismissing;
    public bool Dismissing=>_dismissing;
    public SearchWindow(bool inspect=false,StartMenuPage? start=null, LaunchEntry[]? actions=null, Rect? bounds=null)
    {
        _start=start;
        _actions=actions??[];
        Title=start is null?"IslandBar — поиск приложений":"IslandBar — Пуск";
        var location=start?.Bounds??bounds;
        Width=location?.Width??600;Height=location?.Height??430;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=inspect;Topmost=true;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Segoe UI");
        if(location is { } box){
            WindowStartupLocation=WindowStartupLocation.Manual;
            Left=box.Left;Top=box.Top;
        }
        var root=new DockPanel();
        _frame=PanelControls.Frame(root,new Thickness(22));
        Content=_frame;
        var header=Header(start is null?"Поиск":"Пуск");
        DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var fieldContent=new DockPanel {Height=44};
        var magnifier=new TextBlock {
            Text="\uE721",FontFamily=new FontFamily("Segoe Fluent Icons"),FontSize=16,Foreground=PanelControls.Silver,
            VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,11,0)
        };
        fieldContent.Children.Add(magnifier);
        var searchBar=new Grid();
        var placeholder=new TextBlock {Text=actions is null?"Найти приложение":"Приложение, папка, файл или действие",Foreground=PanelControls.Secondary,FontSize=17,IsHitTestVisible=false,VerticalAlignment=VerticalAlignment.Center};
        searchBar.Children.Add(placeholder);searchBar.Children.Add(_query);fieldContent.Children.Add(searchBar);
        _query.TextChanged+=(_,_)=>placeholder.Visibility=_query.Text.Length==0?Visibility.Visible:Visibility.Hidden;
        System.Windows.Automation.AutomationProperties.SetName(_query,"Название приложения");
        var field=PanelControls.Well(fieldContent,new Thickness(13,0,13,0));
        field.Margin=new Thickness(0,0,0,16);
        _query.GotKeyboardFocus+=(_,_)=>field.BorderBrush=PanelControls.Rim;
        _query.LostKeyboardFocus+=(_,_)=>field.BorderBrush=PanelControls.Outline;
        DockPanel.SetDock(field,Dock.Top);root.Children.Add(field);
        _hint.TextWrapping=TextWrapping.Wrap;
        var footer=new DockPanel {Margin=new Thickness(0,10,0,0)};
        var divider=new Border {Background=PanelControls.Outline,Height=1};
        DockPanel.SetDock(divider,Dock.Top);footer.Children.Add(divider);
        var footerLine=new DockPanel {Margin=new Thickness(0,8,0,0)};
        _hint.Margin=new Thickness(0);_hint.VerticalAlignment=VerticalAlignment.Center;
        DockPanel.SetDock(_keyHints,Dock.Right);footerLine.Children.Add(_keyHints);
        footerLine.Children.Add(_hint);footer.Children.Add(footerLine);
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        if(start is null)root.Children.Add(_results);
        else{
            var main=new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(3,GridUnitType.Star)});
            main.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(2,GridUnitType.Star)});
            var left=new Grid {Margin=new Thickness(0,0,15,0)};
            left.Children.Add(start.Favorites);left.Children.Add(_results);main.Children.Add(left);
            Grid.SetColumn(start.Shortcuts,1);main.Children.Add(start.Shortcuts);
            root.Children.Add(main);
            _results.Visibility=Visibility.Collapsed;
        }
        _results.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,ScrollBarVisibility.Auto);
        _results.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);
        _results.Resources[typeof(ScrollBar)]=PanelControls.ScrollBar;
        var style=new Style(typeof(ListBoxItem));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,8,8,8)));style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        var row=new FrameworkElementFactory(typeof(Border));row.SetValue(Border.CornerRadiusProperty,new CornerRadius(10));row.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));row.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));row.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        style.Setters.Add(new Setter(Control.TemplateProperty,new ControlTemplate(typeof(ListBoxItem)){VisualTree=row}));
        var hover=new Trigger {Property=ListBoxItem.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Control.BackgroundProperty,PanelControls.Tile));style.Triggers.Add(hover);
        var selected=new Trigger {Property=ListBoxItem.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Control.BackgroundProperty,PanelControls.Chrome));style.Triggers.Add(selected);_results.ItemContainerStyle=style;
        _query.TextChanged+=async(_,_)=>await RefreshAsync();
        _results.PreviewMouseLeftButtonUp+=(_,e)=>{
            if(e.OriginalSource is DependencyObject source&&ItemsControl.ContainerFromElement(_results,source) is ListBoxItem item)
                LaunchItem(item);
        };
        PreviewKeyDown+=(_,e)=>{
            if(e.Key==Key.Escape){Dismiss();e.Handled=true;return;}
            if(!_results.IsVisible||(!_query.IsKeyboardFocusWithin&&!_results.IsKeyboardFocusWithin))return;
            if(e.Key==Key.Enter){LaunchSelected();e.Handled=true;}
            else if((e.Key is Key.Down or Key.Up)&&_results.Items.Count>0){int delta=e.Key==Key.Down?1:-1;_results.SelectedIndex=Math.Clamp(_results.SelectedIndex+delta,0,_results.Items.Count-1);if(_results.SelectedItem is not null)_results.ScrollIntoView(_results.SelectedItem);e.Handled=true;}
        };
        Closing+=(_,_)=>_closing=true;
        Loaded+=async(_,_)=>{
            Program.Log(start is null?"search shown":"start menu shown");
            FocusSearch();
            ChromeMotion.Enter(_frame,anchored:start is not null);
            ChromeMotion.Reveal(field);
            if(start is not null){
                if(start.Favorites is FrameworkElement favorites)ChromeMotion.Reveal(favorites,1);
                if(start.Shortcuts is FrameworkElement shortcuts)ChromeMotion.Reveal(shortcuts,3);
            }
            await RefreshAsync();
        };
        if(start is null)Deactivated+=(_,_)=>Dismiss();
    }

    DockPanel Header(string title)
    {
        var row=new DockPanel {Margin=new Thickness(0,0,0,14)};
        var close=PanelControls.Button(PanelControls.Keycap("Esc"),"Закрыть",32,true);
        close.Padding=new Thickness(6,0,6,0);close.VerticalAlignment=VerticalAlignment.Top;
        DockPanel.SetDock(close,Dock.Right);row.Children.Add(close);
        close.Click+=(_,_)=>Dismiss();
        if(_start is not null){
            var glyph=new TextBlock {Text="\uE713",FontFamily=new FontFamily("Segoe Fluent Icons"),FontSize=15,Foreground=PanelControls.Silver};
            var settings=PanelControls.Button(glyph,"Настройки островка",32,true);
            settings.ToolTip=PanelControls.Hint("Настройки островка");settings.VerticalAlignment=VerticalAlignment.Top;
            settings.Click+=(_,_)=>_start.OpenSettings();
            DockPanel.SetDock(settings,Dock.Right);row.Children.Add(settings);
            var windows=PanelControls.Button("Параметры Windows","Параметры Windows",32,true);
            windows.FontSize=11;windows.VerticalAlignment=VerticalAlignment.Top;
            windows.Click+=(_,_)=>OpenWindowsSettings();
            DockPanel.SetDock(windows,Dock.Right);row.Children.Add(windows);
        }
        var text=new StackPanel();
        text.Children.Add(PanelControls.Eyebrow("ОСТРОВОК"));
        text.Children.Add(new TextBlock {Text=title,FontSize=29,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White});
        row.Children.Add(text);
        return row;
    }
    void OpenWindowsSettings()
    {
        try{
            Process.Start(new ProcessStartInfo("ms-settings:") {UseShellExecute=true})?.Dispose();
            _start?.AfterLaunch();
            Dismiss();
        }
        catch(Exception ex){ShowLaunchError("Не удалось открыть параметры Windows");Program.Log("Windows settings: "+ex.Message);}
    }

    async Task RefreshAsync()
    {
        int revision=++_revision;
        string query=_query.Text.Trim();
        bool revealResults=_results.Visibility!=Visibility.Visible;
        if(_start is not null){
            bool searching=query.Length>0;
            bool revealHome=!searching&&_start.Favorites.Visibility!=Visibility.Visible;
            _start.Favorites.Visibility=searching?Visibility.Collapsed:Visibility.Visible;
            _results.Visibility=searching?Visibility.Visible:Visibility.Collapsed;
            if(!searching){
                _results.Items.Clear();
                _hint.Text="Печатай, чтобы найти приложение";
                UpdateKeyHints(false);
                if(revealHome&&_start.Favorites is FrameworkElement home)ChromeMotion.Reveal(home);
                return;
            }
        }
        _results.Items.Clear();
        _hint.Text="Ищу приложения…";
        _keyHints.Children.Clear();
        var index=await _index;
        if(revision!=_revision||_closing||_dismissing)return;
        IEnumerable<LaunchEntry> entries=index;
        if(_start is not null)
            entries=_start.Pinned.Select(app=>new LaunchEntry(app.Name,app.LaunchPath!,true,app))
                .Concat(index).DistinctBy(entry=>entry.Name,StringComparer.CurrentCultureIgnoreCase);
        else entries=_actions.Concat(entries);
        var matches=entries.Where(e=>e.Name.Contains(query,StringComparison.CurrentCultureIgnoreCase)).OrderByDescending(e=>query.Length==0?e.Pinned:e.Name.StartsWith(query,StringComparison.CurrentCultureIgnoreCase)).ThenBy(e=>e.Name).Take(12).ToArray();
        var icons=await Task.Run(()=>matches.Select(e=>e.Favorite?.Icon??(e.Path.EndsWith(".lnk",StringComparison.OrdinalIgnoreCase)?Native.Icon(e.Path):null)).ToArray());
        if(revision!=_revision||!IsLoaded||_closing||_dismissing)return;_results.Items.Clear();
        for(int i=0;i<matches.Length;i++){
            var entry=matches[i];var row=new DockPanel();
            if(icons[i] is {} icon){
                var image=new Image {Source=icon,Width=26,Height=26,Margin=new Thickness(0,0,12,0)};
                RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
                row.Children.Add(image);
            }
            else row.Children.Add(new Border {Width=26,Height=26,CornerRadius=new CornerRadius(7),Background=PanelControls.Chrome,Margin=new Thickness(0,0,12,0),Child=new TextBlock {Text=entry.Name[..1].ToUpperInvariant(),Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontSize=13}});
            row.Children.Add(new TextBlock {Text=entry.Name,VerticalAlignment=VerticalAlignment.Center,FontSize=13,Foreground=Brushes.White,TextTrimming=TextTrimming.CharacterEllipsis});
            var item=new ListBoxItem {Content=row,Tag=entry};System.Windows.Automation.AutomationProperties.SetName(item,entry.Name);_results.Items.Add(item);
        }
        if(_results.Items.Count>0)_results.SelectedIndex=0;
        if(revealResults)ChromeMotion.Reveal(_results);
        _hint.Text=matches.Length==0?"Ничего не найдено. Попробуй другое название.":"Выбери результат";
        UpdateKeyHints(matches.Length>0);
    }
    void UpdateKeyHints(bool hasResults)
    {
        _keyHints.Children.Clear();
        if(hasResults){
            var arrows=PanelControls.Keycap("↑ ↓");arrows.Margin=new Thickness(8,0,5,0);
            _keyHints.Children.Add(arrows);
            _keyHints.Children.Add(PanelControls.Keycap("Enter"));
        }
        else if(_start is not null)_keyHints.Children.Add(PanelControls.Keycap("Ctrl + Esc"));
    }
    void LaunchSelected()=>LaunchItem(_results.SelectedItem as ListBoxItem);
    void LaunchItem(ListBoxItem? item)
    {
        if(item?.Tag is not LaunchEntry entry)return;
        try{
            if(entry.Run is {} run){Dismiss(immediate:true);run();return;}
            if(entry.Favorite is {} app)Catalog.OpenFavorite(app);
            else Process.Start(new ProcessStartInfo(entry.Path){UseShellExecute=true})?.Dispose();
            _start?.AfterLaunch();
            Dismiss();
        }
        catch(Exception ex){ShowLaunchError("Не удалось открыть приложение");Program.Log("search launch: "+ex.Message);}
    }
    public void Dismiss(bool immediate=false)
    {
        if(_closing)return;
        if(immediate){Close();return;}
        if(_dismissing)return;
        _dismissing=true;
        _frame.IsHitTestVisible=false;
        ChromeMotion.Exit(_frame,()=>{if(!_closing)Close();});
    }
    public void FocusSearch()
    {
        if(_closing)return;
        if(!IsActive)Activate();
        _query.Focus();
    }
    public void ShowLaunchError(string message)=>_hint.Text=message;
    public static List<LaunchEntry> BuildIndex()
    {
        var entries=new List<LaunchEntry>();
        string pins=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        foreach(var folder in new[]{pins,Environment.GetFolderPath(Environment.SpecialFolder.Programs),Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)}){
            if(!Directory.Exists(folder))continue;
            try{foreach(string path in Directory.EnumerateFiles(folder,"*.lnk",SearchOption.AllDirectories)){
                string name=Path.GetFileNameWithoutExtension(path);if(name.Contains("uninstall",StringComparison.OrdinalIgnoreCase)||name.StartsWith("Удал",StringComparison.OrdinalIgnoreCase)||name.Contains("деинстал",StringComparison.OrdinalIgnoreCase))continue;
                entries.Add(new(name,path,folder==pins));
            }}catch(Exception ex){Program.Log("search index: "+ex.Message);}
        }
        dynamic? shell=null;
        try {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);dynamic folder=shell!.NameSpace("shell:AppsFolder");dynamic items=folder.Items();
            try{for(int i=0;i<items.Count;i++){dynamic item=items.Item(i);try{string name=item.Name;string path=item.Path;if(!string.IsNullOrWhiteSpace(name)&&!string.IsNullOrWhiteSpace(path))entries.Add(new(name,"shell:AppsFolder\\"+path));}finally{Marshal.ReleaseComObject(item);}}}
            finally{Marshal.ReleaseComObject(items);Marshal.ReleaseComObject(folder);}
        }catch(Exception ex){Program.Log("apps index: "+ex.Message);}
        finally{if(shell is not null)Marshal.ReleaseComObject(shell);}
        return entries.DistinctBy(e=>e.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
