using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace IslandBar;

static class WindowLayouts
{
    [StructLayout(LayoutKind.Sequential)]struct MonitorInfo {public uint Size;public Native.Rect Monitor,Work;public uint Flags;}
    [DllImport("user32.dll")]static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("dwmapi.dll")]static extern int DwmGetWindowAttribute(nint window,uint attribute,out Native.Rect rect,uint size);
    public static Rect WorkArea(nint window)
    {
        var info=new MonitorInfo {Size=(uint)Marshal.SizeOf<MonitorInfo>()};
        if(!GetMonitorInfo(MonitorFromWindow(window,2),ref info))throw new InvalidOperationException("Не удалось получить размер монитора");
        var work=info.Work;int bottom=work.Bottom;
        if(info.Monitor.Left==0&&info.Monitor.Top==0)bottom=Math.Min(bottom,info.Monitor.Bottom-88);
        return new Rect(work.Left+12,work.Top+12,Math.Max(1,work.Right-work.Left-24),Math.Max(1,bottom-work.Top-24));
    }
    public static Rect[] Calculate(Rect area,int layout)
    {
        const double gap=12;
        if(layout is 2 or 3)return Enumerable.Range(0,layout).Select(i=>new Rect(area.Left+i*(area.Width+gap)/layout,area.Top,(area.Width-gap*(layout-1))/layout,area.Height)).ToArray();
        if(layout==4){double w=(area.Width-gap)/2,h=(area.Height-gap)/2;return Enumerable.Range(0,4).Select(i=>new Rect(area.Left+(i%2)*(w+gap),area.Top+(i/2)*(h+gap),w,h)).ToArray();}
        double main=(area.Width-gap)*.64,side=area.Width-main-gap,half=(area.Height-gap)/2;
        return [new(area.Left,area.Top,main,area.Height),new(area.Left+main+gap,area.Top,side,half),new(area.Left+main+gap,area.Top+half+gap,side,half)];
    }
    public static Native.Rect VisibleBounds(nint window)
    {
        if(DwmGetWindowAttribute(window,9,out var visible,16)==0&&visible.Right>visible.Left&&visible.Bottom>visible.Top)return visible;
        Native.GetWindowRect(window,out var outer);return outer;
    }
    public static void Apply(nint[] windows,int layout)
    {
        int required=layout==5?3:layout;if(windows.Length!=required)throw new InvalidOperationException($"Для этой раскладки выбери {required} окна");
        var boxes=Calculate(WorkArea(windows[0]),layout);
        for(int i=0;i<windows.Length;i++){
            nint window=windows[i];if(!Native.IsWindow(window))continue;
            if(Native.IsIconic(window)||Native.IsZoomed(window))Native.ShowWindow(window,9);
            int l=0,t=0,r=0,b=0;
            if(Native.GetWindowRect(window,out var outer)&&DwmGetWindowAttribute(window,9,out var visible,16)==0&&visible.Right>visible.Left&&visible.Bottom>visible.Top){l=visible.Left-outer.Left;t=visible.Top-outer.Top;r=outer.Right-visible.Right;b=outer.Bottom-visible.Bottom;}
            var box=boxes[i];if(!Native.SetWindowPos(window,0,(int)Math.Round(box.Left)-l,(int)Math.Round(box.Top)-t,(int)Math.Round(box.Width)+l+r,(int)Math.Round(box.Height)+t+b,0x14))throw new InvalidOperationException("Windows не разрешила переместить одно из окон");
        }
    }
}

sealed partial class IslandWindow
{
    SearchWindow? _searchWindow;
    bool _searchHotkey;
    void OpenSearch()
    {
        Position();
        if (_closing || UpdateFullscreenVisibility()) return;
        Collapse();
        if(_searchWindow is {Dismissing:false}){_searchWindow.FocusSearch();return;}
        _searchWindow?.Dismiss(immediate:true);
        var area = DockDisplayArea();
        var width = Math.Min(600, area.Width - 24);
        var height = Math.Min(430, area.Height - 24);
        var bounds = new Rect(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
        _searchWindow=new SearchWindow(_preview||_inspect, actions: PaletteEntries(), bounds: bounds);
        _searchWindow.Closed+=(_,_)=>_searchWindow=null;_searchWindow.Show();
    }

    LaunchEntry[] PaletteEntries()
    {
        var entries = new List<LaunchEntry> {
            new("Очередь переносов", "", true, Run: OpenTransferQueue),
            new("Проекты хранилища", "", true, Run: () => { EnsureStorage(); _storageWindow?.OpenProjects(); }),
            new("Хранилище файлов", "", true, Run: EnsureStorage),
            new("Настройки островка", "", true, Run: OpenSettings),
        };
        foreach (var project in StorageProjects.Load())
            entries.Add(new(project.Name + " · проект", "", Run: () => { EnsureStorage(); _storageWindow?.OpenProject(project); }));
        foreach (var folder in QuickFolders.All())
            entries.Add(new(folder.Name + " · папка", "", Run: () => { EnsureStorage(); _storageWindow?.OpenLocation(folder.Path); }));
        foreach (var path in StorageRecent.Files())
            entries.Add(new(Path.GetFileName(path), path));
        return [..entries];
    }
    void OpenLayouts()
    {
        var column=CardColumn();column.Children.Add(CardTitle("Раскладка окон"));
        var hint=LeftLabel("Выбери окна и раскладку",12,Secondary);hint.Margin=new Thickness(0,0,0,8);column.Children.Add(hint);
        var list=new StackPanel();var candidates=new List<(CheckBox Check,nint Window)>();nint foreground=Native.GetForegroundWindow();
        var windows=_tiles.Values.Where(t=>!t.App.Key.Contains("Koala",StringComparison.OrdinalIgnoreCase)).SelectMany(t=>t.App.Windows.Select(w=>(Window:w,Name:Native.Title(w)))).Where(w=>Native.IsWindowVisible(w.Window)).OrderByDescending(w=>w.Window==foreground).DistinctBy(w=>w.Window);
        foreach(var item in windows){var text=LeftLabel(item.Name,12);var check=new CheckBox {Content=text,Tag=item.Window,IsChecked=candidates.Count<2,Margin=new Thickness(0,7,0,7),Template=PanelControls.CheckBox,ToolTip=PanelControls.Hint(item.Name)};System.Windows.Automation.AutomationProperties.SetName(check,item.Name);list.Children.Add(check);candidates.Add((check,item.Window));}
        if(candidates.Count==0)list.Children.Add(LeftLabel("Нет открытых окон",12,Secondary));
        column.Children.Add(PanelControls.Scroller(list,175));
        var buttons=new System.Windows.Controls.Primitives.UniformGrid {Columns=2,Margin=new Thickness(-3,10,-3,0)};
        foreach(var choice in new[]{("Две колонки",2),("Три колонки",3),("Четыре окна",4),("Главное + два",5)}){
            var content=new StackPanel {Orientation=Orientation.Horizontal};
            var badge=new Canvas {Width=23,Height=17,Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center};
            foreach(var box in WindowLayouts.Calculate(new Rect(0,0,115,85),choice.Item2)){
                var region=new Border {Width=box.Width/5,Height=box.Height/5,Background=PanelControls.Secondary,CornerRadius=new CornerRadius(1)};
                Canvas.SetLeft(region,box.Left/5);Canvas.SetTop(region,box.Top/5);badge.Children.Add(region);
            }
            content.Children.Add(badge);content.Children.Add(LeftLabel(choice.Item1,11));
            var button=PanelControls.Button(content,choice.Item1,37,true);
            button.Margin=new Thickness(3);PanelControls.FinishTile(button);button.IsEnabled=candidates.Count>0;
            button.Click+=(_,_)=>{
                var selected=candidates.Where(c=>c.Check.IsChecked==true).Select(c=>c.Window).ToArray();int required=choice.Item2==5?3:choice.Item2;
                if(selected.Length!=required){PanelError($"Для этой раскладки выбери {required} окна");return;}
                try{WindowLayouts.Apply(selected,choice.Item2);CloseFeature();Collapse();}catch(Exception ex){PanelError(ex.Message);}
            };buttons.Children.Add(button);
        }
        column.Children.Add(buttons);OpenCard("layouts",column);
    }
}
