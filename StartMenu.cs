using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace IslandBar;

sealed partial class IslandWindow
{
    Button _startButton=null!;
    SearchWindow? _startMenu;

    Button CreateStartButton()
    {
        var button=MakeButton(WindowsLogo(),"Пуск островка",40);
        button.Click+=(_,_)=>ToggleStartMenu();
        return button;
    }

    void ToggleStartMenu()
    {
        if (_closing || UpdateFullscreenVisibility()) return;
        if(_startMenu is {Dismissing:false}){CloseStartMenu();return;}
        CloseStartMenu(immediate:true);
        _searchWindow?.Dismiss(immediate:true);
        CloseFeature(immediate:true);ClosePreview();CloseMenus();
        if(_startButton.ToolTip is ToolTip tip)tip.IsOpen=false;

        var pinned=OrderedKeys().Select(key=>_tiles[key].App)
            .Where(app=>app.Pinned&&app.LaunchPath is not null).ToArray();
        bool settled=_expanded&&!_rendering;
        if(!_expanded)Expand();
        var dpi=VisualTreeHelper.GetDpi(this);
        double left=settled?_startButton.PointToScreen(new Point(0,0)).X/dpi.DpiScaleX:Left+(Width-_openWidth)/2+9;
        var area=DockDisplayArea();
        double width=Math.Min(588,area.Width-24);
        double bottom=Top+Height-_frame.Margin.Bottom-TargetHeight-10;
        double height=Math.Min(466,Math.Max(260,bottom-area.Top-12));
        var bounds=new Rect(
            Math.Clamp(left,area.Left+12,area.Right-width-12),
            Math.Max(area.Top+12,bottom-height),width,height);

        var page=new StartMenuPage(bounds,StartFavorites(pinned),StartShortcuts(),pinned,()=>Collapse(),OpenSettings);
        var menu=new SearchWindow(_preview||_inspect,page);
        _startMenu=menu;
        UpdatePanelMarker();
        ToolTipService.SetIsEnabled(_startButton,false);
        menu.Deactivated+=(_,_)=>Dispatcher.BeginInvoke(()=>{
            if(ReferenceEquals(_startMenu,menu)&&!menu.IsActive&&Native.GetForegroundWindow()!=_handle)menu.Dismiss();
        },DispatcherPriority.Background);
        menu.Closed+=(_,_)=>{
            if(ReferenceEquals(_startMenu,menu))_startMenu=null;
            UpdatePanelMarker();
            SyncOutsideTracking();
            if(_expanded&&!_rendering)ToolTipService.SetIsEnabled(_startButton,true);
            WriteStatus();
        };
        menu.Show();
        menu.FocusSearch();
        SyncOutsideTracking();
        WriteStatus();
    }

    UIElement StartFavorites(IReadOnlyList<DockApp> pinned)
    {
        var section=new DockPanel();
        var header=new DockPanel {Margin=new Thickness(0,2,4,9)};
        if(pinned.Count>0){
            var count=PanelControls.Keycap(pinned.Count.ToString("00"));
            DockPanel.SetDock(count,Dock.Right);header.Children.Add(count);
        }
        header.Children.Add(PanelControls.Eyebrow("ИЗБРАННОЕ"));
        DockPanel.SetDock(header,Dock.Top);section.Children.Add(header);
        if(pinned.Count==0){
            var empty=LeftLabel("Закрепи приложение через ПКМ на его значке в островке.",12,Secondary);
            empty.TextWrapping=TextWrapping.Wrap;section.Children.Add(empty);
            return section;
        }

        var tiles=new UniformGrid {Columns=3,VerticalAlignment=VerticalAlignment.Top};
        foreach(var app in pinned){
            var icon=app.Icon is {} source
                ?(FrameworkElement)new Image {Source=source,Width=30,Height=30}
                :Glyph("\uE737",22);
            icon.Height=30;
            RenderOptions.SetBitmapScalingMode(icon,BitmapScalingMode.HighQuality);
            var lens=new Border {
                Width=42,Height=42,Child=icon,Background=PanelControls.Chrome,BorderBrush=PanelControls.Outline,
                BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(14),HorizontalAlignment=HorizontalAlignment.Center
            };
            var label=Label(app.Name,11,FontWeights.Medium);
            label.Margin=new Thickness(0,6,0,0);label.TextTrimming=TextTrimming.CharacterEllipsis;
            var content=new StackPanel();
            content.Children.Add(lens);content.Children.Add(label);
            var button=PanelControls.Button(content,app.Name,84);
            button.Padding=new Thickness(6,0,6,0);
            button.Margin=new Thickness(0,0,6,6);
            PanelControls.FinishTile(button);
            button.Click+=(_,_)=>RunStartAction(()=>Catalog.OpenFavorite(app),"Не удалось открыть приложение");
            int index=tiles.Children.Count;
            button.Loaded+=(_,_)=>ChromeMotion.Reveal(button,index);
            tiles.Children.Add(button);
        }
        section.Children.Add(PanelControls.Scroller(tiles,double.PositiveInfinity));
        return section;
    }

    UIElement StartShortcuts()
    {
        var rows=new Grid();
        rows.RowDefinitions.Add(new RowDefinition {Height=new GridLength(5,GridUnitType.Star)});
        rows.RowDefinitions.Add(new RowDefinition {Height=new GridLength(3,GridUnitType.Star)});
        var folders=new StackPanel();
        var storage=StartShortcut("\uE8B7","Хранилище");
        storage.Click+=(_,_)=>OpenStorage();folders.Children.Add(storage);
        foreach(var folder in QuickFolders.All()){
            var button=StartShortcut(folder.Glyph,folder.Name);
            button.Click+=(_,_)=>RunStartAction(()=>ShowFolder(folder.Path),"Не удалось открыть папку");
            folders.Children.Add(button);
        }
        var folderSection=StartSection("Быстрые папки",folders,OpenFolders,112);
        folderSection.Margin=new Thickness(0,0,0,6);
        rows.Children.Add(folderSection);

        var sets=new StackPanel();
        foreach(var set in AvailableSets()){
            var button=StartShortcut("\uE8F1",set.Name);
            button.Click+=async(_,_)=>await RunSetAsync(set);
            sets.Children.Add(button);
        }
        if(sets.Children.Count==0){
            var add=StartShortcut("\uE710","Создать набор");
            add.Click+=(_,_)=>EditSet(null);
            sets.Children.Add(add);
        }
        var setSection=StartSection("Наборы",sets,OpenSets,double.PositiveInfinity);
        setSection.Margin=new Thickness(0,5,0,0);
        Grid.SetRow(setSection,1);rows.Children.Add(setSection);
        return PanelControls.Well(rows,new Thickness(8,6,8,6),15);
    }

    DockPanel StartSection(string title,StackPanel list,Action manage,double maxHeight)
    {
        var section=new DockPanel();
        var headingLine=new DockPanel();
        var chevron=LeftLabel("›",15,Secondary);DockPanel.SetDock(chevron,Dock.Right);headingLine.Children.Add(chevron);
        headingLine.Children.Add(PanelControls.Eyebrow(title.ToUpperInvariant()));
        var heading=PanelControls.Button(headingLine,title,29);
        heading.Padding=new Thickness(6,0,6,0);
        heading.Click+=(_,_)=>{CloseStartMenu(immediate:true);manage();};
        DockPanel.SetDock(heading,Dock.Top);section.Children.Add(heading);
        var scroll=PanelControls.Scroller(list,maxHeight);scroll.VerticalAlignment=VerticalAlignment.Top;
        section.Children.Add(scroll);
        return section;
    }

    static Button StartShortcut(string glyph,string title)
    {
        var row=new DockPanel();
        var icon=Glyph(glyph,14);icon.Width=20;icon.Margin=new Thickness(0,0,6,0);icon.Foreground=Secondary;
        row.Children.Add(icon);row.Children.Add(LeftLabel(title,12));
        var button=PanelControls.Button(row,title,28);
        button.Padding=new Thickness(6,0,6,0);
        return button;
    }

    void RunStartAction(Action action,string error)
    {
        try{action();Collapse();}
        catch(Exception ex){_startMenu?.ShowLaunchError(error);Program.Log("start menu: "+ex.Message);}
    }

    void CloseStartMenu(bool immediate=false)
    {
        _startMenu?.Dismiss(immediate);
        UpdatePanelMarker();
    }

    bool InsideStartMenu(int x,int y)
    {
        if(_startMenu is not {IsVisible:true,Dismissing:false} menu)return false;
        nint hwnd=new WindowInteropHelper(menu).Handle;
        return Native.GetWindowRect(hwnd,out var rect)&&x>=rect.Left&&x<rect.Right&&y>=rect.Top&&y<rect.Bottom;
    }
}
