using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace IslandBar;

sealed partial class IslandWindow
{
    readonly HardwareMonitor _hardwareMonitor=new();
    readonly DownloadMonitor _downloadMonitor=new();
    PcState? _pcState;
    bool _hardwareReading,_downloadsReading;
    readonly Queue<PcState> _pcHistory=new();
    readonly HashSet<string> _notifiedDownloads=new(StringComparer.OrdinalIgnoreCase);
    readonly TextBlock _pcCompact=Label("—",10);
    readonly TextBlock _downloadBadge=Label("",8,FontWeights.SemiBold);
    readonly Border _downloadProgress=new() {Height=2,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,Background=Accent,CornerRadius=new CornerRadius(1),Margin=new Thickness(4,0,4,1),Width=0};
    readonly Dictionary<string,DownloadRow> _downloadRows=new(StringComparer.OrdinalIgnoreCase);
    StackPanel? _downloadList;
    TextBlock? _downloadHeading,_downloadEmpty,_pcCpu,_pcGpu,_pcCpuTemp,_pcGpuTemp,_pcRam,_pcVram,_pcSource;
    Border? _cpuMeter,_gpuMeter;
    Polyline? _cpuPlot,_gpuPlot;
    Action? _toastAction;
    sealed record DownloadRow(Border Root,TextBlock Title,TextBlock Detail,TextBlock Status,ProgressBar Progress,Button Show);

    void InitializeExtras()
    {
        var files=MakeButton(Glyph("\uE8B7",17),"Файлы · Хранилище",32);
        files.Click+=(_,_)=>OpenStorage();_featureTriggers["storage"]=files;
        var folders=MakeButton(Glyph("\uE840",17),"Быстрые папки",32);folders.Click+=(_,_)=>OpenFolders();_featureTriggers["folders"]=folders;
        var downloadVisual=new Grid {Width=32,Height=34};downloadVisual.Children.Add(Glyph("\uE896",17));
        _downloadBadge.HorizontalAlignment=HorizontalAlignment.Right;_downloadBadge.VerticalAlignment=VerticalAlignment.Top;_downloadBadge.Foreground=Accent;_downloadBadge.Visibility=Visibility.Hidden;
        downloadVisual.Children.Add(_downloadBadge);downloadVisual.Children.Add(_downloadProgress);
        var downloads=MakeButton(downloadVisual,"Загрузки браузера",36);downloads.Click+=(_,_)=>OpenDownloads();_featureTriggers["downloads"]=downloads;
        var pcVisual=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};pcVisual.Children.Add(Glyph("\uE9D9",17));_pcCompact.Margin=new Thickness(5,0,0,0);pcVisual.Children.Add(_pcCompact);
        var pc=MakeButton(pcVisual,"Температуры и нагрузка ПК",58);pc.Click+=(_,_)=>OpenPc();_featureTriggers["pc"]=pc;
        int index=_dock.Children.IndexOf(_scroll);foreach(var button in new[]{files,folders,downloads,pc})_dock.Children.Insert(index++,button);
    }
    void OpenFolders() => OpenFolders(null, null);

    void OpenFolders(string? showPath, string? message)
    {
        var column=CardColumn(456);column.Children.Add(CardTitle("Быстрые папки"));
        var add=PanelControls.Button("+  Добавить папки","Добавить быстрые папки",36,true);
        PanelControls.FinishTile(add);
        add.Click+=(_,_)=>{
            CloseFeature(immediate:true);
            Collapse();
            string[] added=[];
            string? feedback=null;
            try {
                var selected=QuickFolders.Choose(this);
                if(selected.Length!=0) {
                    added=QuickFolders.AddMany(selected);
                    feedback=added.Length==0?"Выбранные папки уже закреплены или недоступны.":$"Добавлено папок: {added.Length}";
                }
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
                feedback="Не удалось сохранить быстрые папки.";
                Program.Log("quick folders: "+ex.Message);
            }
            Expand();OpenFolders(added.FirstOrDefault(),feedback);
        };
        column.Children.Add(add);
        var hint=LeftLabel(message??"Перетащи карточку за точки, чтобы изменить порядок",11,Secondary);
        hint.Margin=new Thickness(0,9,0,10);
        column.Children.Add(hint);
        var list=new UniformGrid {Columns=2,VerticalAlignment=VerticalAlignment.Top};
        var rows=new Dictionary<string,Border>(StringComparer.OrdinalIgnoreCase);
        var custom=QuickFolders.Custom().Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var reorder=new PlaceReorder(list,QuickFolders.SaveOrder,PanelError);
        foreach(var folder in QuickFolders.All()){
            var visual=new DockPanel();
            var icon=Glyph(folder.Glyph,18);icon.Foreground=PanelControls.Silver;
            var badge=PanelControls.Well(icon,new Thickness(0),8);
            badge.Width=32;badge.Height=32;badge.Background=PanelControls.Chrome;badge.Margin=new Thickness(0,0,8,0);
            visual.Children.Add(badge);
            var dots=Label("⠿",16);dots.Foreground=Secondary;
            var grip=new Border {
                Width=21, Height=34, Margin=new Thickness(4,0,0,0), Background=Brushes.Transparent,
                Cursor=Cursors.SizeAll, Child=dots
            };
            DockPanel.SetDock(grip,Dock.Right);visual.Children.Add(grip);
            var labels=new StackPanel {VerticalAlignment=VerticalAlignment.Center};
            labels.Children.Add(LeftLabel(folder.Name,13));
            var path=LeftLabel(folder.Path,10,Secondary);path.Margin=new Thickness(0,2,0,0);
            labels.Children.Add(path);visual.Children.Add(labels);
            var button=PanelControls.Button(visual,folder.Name,62);
            button.Padding=new Thickness(8,0,6,0);PanelControls.FinishTile(button);
            path.ToolTip=PanelControls.Hint(folder.Path);ToolTipService.SetInitialShowDelay(path,900);
            System.Windows.Automation.AutomationProperties.SetHelpText(button,folder.Path);
            button.Click+=(_,_)=>{try{ShowFolder(folder.Path);CloseFeature();Collapse();}catch(Exception ex){PanelError("Не удалось открыть папку");Program.Log("folder: "+ex.Message);}};
            var row=new Border {Child=button,Tag=folder.Path,AllowDrop=true,CornerRadius=new CornerRadius(12),
                BorderThickness=new Thickness(1),Margin=new Thickness(3,0,3,8)};
            rows.Add(folder.Path,row);
            reorder.Bind(row,grip);
            var menu=CreateMenu();TrackMenu(menu);
            var up=new MenuItem {Header="Переместить на позицию раньше"};
            var down=new MenuItem {Header="Переместить на позицию позже"};
            menu.Opened+=(_,_)=>{
                int index=list.Children.IndexOf(row);
                up.IsEnabled=index>0;
                down.IsEnabled=index<list.Children.Count-1;
            };
            up.Click+=(_,_)=>reorder.Move(row,list.Children.IndexOf(row)-1);
            down.Click+=(_,_)=>reorder.Move(row,list.Children.IndexOf(row)+1);
            menu.Items.Add(up);menu.Items.Add(down);
            if(custom.Contains(folder.Path)){
                var remove=new MenuItem {Header="Убрать из быстрых папок"};
                remove.Click+=(_,_)=>{
                    try{QuickFolders.Remove(folder.Path);CloseFeature(immediate:true);OpenFolders();}
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){
                        PanelError("Не удалось убрать закрепление");Program.Log("quick folder remove: "+ex.Message);
                    }
                };
                menu.Items.Add(remove);
            }
            button.ContextMenu=menu;
            list.Children.Add(row);
        }
        reorder.Scroll=PanelControls.Scroller(list,288);
        column.Children.Add(reorder.Scroll);
        if(showPath is not null&&rows.TryGetValue(showPath,out var addedRow))
            addedRow.Loaded+=(_,_)=>addedRow.BringIntoView();
        OpenCard("folders",column);
    }
    internal static void ShowFolder(string path,bool select=false)
    {
        string? folder=select?System.IO.Path.GetDirectoryName(path):path;
        if(string.IsNullOrWhiteSpace(folder)||!Directory.Exists(folder))throw new DirectoryNotFoundException(folder);
        var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=false};
        if(select&&File.Exists(path)){start.ArgumentList.Add("/select,");start.ArgumentList.Add(path);}else start.ArgumentList.Add(folder);
        Process.Start(start)?.Dispose();
    }
    void OpenDownloads()
    {
        var column=CardColumn();column.Children.Add(CardTitle("Загрузки"));
        _downloadHeading=LeftLabel("",12,Secondary);_downloadHeading.Margin=new Thickness(0,0,0,8);column.Children.Add(_downloadHeading);
        _downloadList=new StackPanel();_downloadRows.Clear();column.Children.Add(PanelControls.Scroller(_downloadList,300));
        _downloadEmpty=LeftLabel("Здесь появятся загрузки из браузера",12,Secondary);_downloadEmpty.TextWrapping=TextWrapping.Wrap;_downloadEmpty.Margin=new Thickness(0,8,0,20);column.Children.Add(_downloadEmpty);
        var open=PanelControls.Button(LeftLabel("Открыть папку загрузок",12),"Открыть папку загрузок",36,true);
        PanelControls.FinishTile(open);open.Click+=(_,_)=>{try{ShowFolder(QuickFolders.Downloads);CloseFeature();Collapse();}catch(Exception ex){PanelError("Не удалось открыть папку загрузок");Program.Log("downloads folder: "+ex.Message);}};column.Children.Add(open);
        OpenCard("downloads",column);UpdateDownloadsPanel();
    }
    static string Bytes(long bytes)
    {
        if(bytes>=1073741824)return (bytes/1073741824d).ToString("0.0",CultureInfo.CurrentCulture)+" ГБ";
        if(bytes>=1048576)return (bytes/1048576d).ToString("0.0",CultureInfo.CurrentCulture)+" МБ";
        if(bytes>=1024)return (bytes/1024d).ToString("0",CultureInfo.CurrentCulture)+" КБ";
        return bytes+" Б";
    }
    void UpdateDownloadsPanel()
    {
        if(_featureKind!="downloads"||_downloadList is null)return;
        var items=_downloadMonitor.Current;int active=items.Count(d=>d.Active);_downloadHeading!.Text=active>0?$"Загружается: {active}":"Нет активных загрузок";_downloadEmpty!.Visibility=items.Count==0?Visibility.Visible:Visibility.Collapsed;
        var keys=items.Select(d=>d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var key in _downloadRows.Keys.Where(k=>!keys.Contains(k)).ToArray()){_downloadList.Children.Remove(_downloadRows[key].Root);_downloadRows.Remove(key);}
        for(int i=0;i<items.Count;i++){
            var item=items[i];if(!_downloadRows.TryGetValue(item.Key,out var row)){
                var content=new StackPanel();var title=LeftLabel(item.Name,12);title.FontWeight=FontWeights.SemiBold;content.Children.Add(title);
                var detail=LeftLabel("",10,Secondary);detail.Margin=new Thickness(0,5,0,5);content.Children.Add(detail);
                var progress=new ProgressBar {Minimum=0,Maximum=100,Height=3,Background=PanelControls.Outline,Foreground=Accent};content.Children.Add(progress);
                var footer=new DockPanel {Margin=new Thickness(0,4,0,0)};var status=LeftLabel("",10,Secondary);footer.Children.Add(status);
                var show=PanelControls.Button(LeftLabel("Папка ↗",11,Secondary),"Показать "+item.Name+" в папке",28,true);show.HorizontalAlignment=HorizontalAlignment.Right;show.Click+=(_,_)=>{
                    try{var latest=_downloadMonitor.Current.FirstOrDefault(d=>d.Key==item.Key)??item;string target=File.Exists(latest.Target)?latest.Target:latest.Current;CloseFeature();Collapse();ShowFolder(target,true);}catch(Exception ex){PanelError("Файл больше не найден");Program.Log("show download: "+ex.Message);}
                };footer.Children.Add(show);content.Children.Add(footer);
                var root=new Border {Child=content,Background=PanelControls.Tile,CornerRadius=new CornerRadius(10),Padding=new Thickness(12),Margin=new Thickness(0,0,0,8)};
                ChromeMotion.Reveal(root,i);
                row=new(root,title,detail,status,progress,show);_downloadRows[item.Key]=row;
            }
            int index=_downloadList.Children.IndexOf(row.Root);if(index!=i){if(index>=0)_downloadList.Children.Remove(row.Root);_downloadList.Children.Insert(i,row.Root);}
            row.Title.Text=item.Name;row.Detail.Text=item.Total>0?Bytes(item.Received)+" из "+Bytes(item.Total):Bytes(item.Received);
            row.Status.Text=item.Active?(item.Percent is double percent?percent.ToString("0")+"%  ·  "+item.Browser:"Загружается · "+item.Browser):item.State==1?"Готово":item.State==2?"Отменено":"Прервано";
            row.Progress.IsIndeterminate=item.Active&&item.Total<=0;row.Progress.Value=item.State==1?100:item.Percent??0;row.Progress.Visibility=item.Active?Visibility.Visible:Visibility.Collapsed;
            row.Show.IsEnabled=Directory.Exists(System.IO.Path.GetDirectoryName(item.Target));
        }
    }
    void OpenPc()
    {
        var column=CardColumn();column.Children.Add(CardTitle("Мониторинг ПК"));
        var metrics=new System.Windows.Controls.Primitives.UniformGrid {Columns=2,Margin=new Thickness(0,0,0,14)};
        (Border Root,TextBlock Load,TextBlock Temp,Border Fill) Metric(string name){
            var content=new StackPanel();content.Children.Add(LeftLabel(name,10,Secondary));var load=LeftLabel("—",25);load.Margin=new Thickness(0,8,0,3);content.Children.Add(load);var temp=LeftLabel("—",12,Secondary);content.Children.Add(temp);
            var fill=new Border {Width=0,Height=3,Background=Accent,HorizontalAlignment=HorizontalAlignment.Left,CornerRadius=new CornerRadius(2)};
            content.Children.Add(new Border {Child=fill,Height=3,Background=PanelControls.Outline,CornerRadius=new CornerRadius(2),Margin=new Thickness(0,13,0,0)});
            return (new Border {Child=content,Padding=new Thickness(13),Background=PanelControls.Tile,CornerRadius=new CornerRadius(10)},load,temp,fill);
        }
        var cpu=Metric("Процессор");var gpu=Metric("Видеокарта");
        cpu.Root.Margin=new Thickness(0,0,4,0);gpu.Root.Margin=new Thickness(4,0,0,0);
        _pcCpu=cpu.Load;_pcCpuTemp=cpu.Temp;_cpuMeter=cpu.Fill;_pcGpu=gpu.Load;_pcGpuTemp=gpu.Temp;_gpuMeter=gpu.Fill;metrics.Children.Add(cpu.Root);metrics.Children.Add(gpu.Root);column.Children.Add(metrics);
        column.Children.Add(LeftLabel("Нагрузка за последние 45 секунд",10,Secondary));var chart=new Canvas {Width=324,Height=55,Margin=new Thickness(0,10,0,15),ClipToBounds=true};
        _cpuPlot=new Polyline {Stroke=Accent,StrokeThickness=1.6};_gpuPlot=new Polyline {Stroke=new SolidColorBrush(Color.FromRgb(120,123,132)),StrokeThickness=1.2};chart.Children.Add(_gpuPlot);chart.Children.Add(_cpuPlot);column.Children.Add(chart);
        _pcRam=LeftLabel("",12);_pcVram=LeftLabel("",11,Secondary);_pcVram.Margin=new Thickness(0,6,0,0);column.Children.Add(_pcRam);column.Children.Add(_pcVram);
        _pcSource=LeftLabel("",10,Secondary);_pcSource.TextWrapping=TextWrapping.Wrap;_pcSource.Margin=new Thickness(0,17,0,0);column.Children.Add(_pcSource);
        OpenCard("pc",column);UpdatePcPanel();
    }
    void UpdatePcPanel()
    {
        if(_featureKind!="pc"||_pcState is not {} pc)return;
        static string Load(double? value)=>value is double number?number.ToString("0")+"%":"—";
        static string Temp(double? value)=>value is double number?number.ToString("0")+" °C":"Датчик недоступен";
        _pcCpu!.Text=Load(pc.CpuLoad);_pcGpu!.Text=Load(pc.GpuLoad);_pcCpuTemp!.Text=Temp(pc.CpuTemperature);_pcGpuTemp!.Text=Temp(pc.GpuTemperature);
        UpdateMeter(_cpuMeter!,pc.CpuLoad);UpdateMeter(_gpuMeter!,pc.GpuLoad);
        _pcRam!.Text=$"ОЗУ   {pc.RamUsedGb:0.0} / {pc.RamTotalGb:0.0} ГБ";_pcVram!.Text=pc.VramUsedMb is double memory?$"Видеопамять   {memory/1024:0.0} ГБ":"Видеопамять   —";
        _pcSource!.Text=pc.SensorSource=="MSI Afterburner"?"Датчики: MSI Afterburner · CPU — серебристый, GPU — серый":"Загрузка и память: Windows. Для температур запусти MSI Afterburner.";
        var history=_pcHistory.ToArray();PointCollection Plot(Func<PcState,double?> get){var points=new PointCollection();for(int i=0;i<history.Length;i++)if(get(history[i]) is double value)points.Add(new Point(324d*(45-history.Length+i)/44,53-Math.Clamp(value,0,100)*.51));return points;}
        _cpuPlot!.Points=Plot(p=>p.CpuLoad);_gpuPlot!.Points=Plot(p=>p.GpuLoad);
    }
    static void UpdateMeter(Border meter,double? load)
    {
        double width=126*Math.Clamp(load??0,0,100)/100;
        if(Math.Abs(meter.Width-width)<.5)return;
        if(!ChromeMotion.Enabled){meter.BeginAnimation(WidthProperty,null);meter.Width=width;return;}
        meter.BeginAnimation(WidthProperty,new DoubleAnimation(width,TimeSpan.FromMilliseconds(350)){
            EasingFunction=new CubicEase {EasingMode=EasingMode.EaseOut}
        });
    }
    async Task RefreshHardwareAsync()
    {
        if(_hardwareReading||_closing)return;_hardwareReading=true;
        try{var state=await Task.Run(_hardwareMonitor.Read);if(_closing)return;_pcState=state;_pcHistory.Enqueue(state);while(_pcHistory.Count>45)_pcHistory.Dequeue();_pcCompact.Text=state.CpuLoad is double cpu?cpu.ToString("0")+"%":"—";UpdatePcPanel();}
        catch(Exception ex){Program.Log("hardware: "+ex.Message);}
        finally{_hardwareReading=false;}
    }
    async Task RefreshDownloadsAsync()
    {
        if(_downloadsReading||_closing)return;_downloadsReading=true;
        try{
            var items=await Task.Run(_downloadMonitor.Read);if(_closing)return;var active=items.Where(d=>d.Active).ToArray();
            _downloadBadge.Text=active.Length>9?"9+":active.Length.ToString();_downloadBadge.Visibility=active.Length>0?Visibility.Visible:Visibility.Hidden;
            long total=active.Sum(d=>Math.Max(0,d.Total)),received=active.Sum(d=>d.Received);_downloadProgress.Width=active.Length==0?0:active.Any(d=>d.Total<=0)?12:Math.Clamp(received*24d/Math.Max(1,total),1,24);
            foreach(var item in items.Where(d=>d.State==1&&d.Observed&&_notifiedDownloads.Add(d.Key))){
                RememberIslandEvent("Загружено · "+item.Name,OpenDownloads);
                if(_preferences.Toasts&&!_expanded&&!_fullscreen&&!_featurePopup.IsOpen){
                    _toastCover.Source=DownloadCompleteIcon();_toastTitle.Text="Готово · "+item.Name;SetToastAction("Открыть загрузки",OpenDownloads);_toastUntil=DateTime.UtcNow.AddSeconds(5);BeginMotion();
                }
            }
            UpdateDownloadsPanel();
        }catch(Exception ex){Program.Log("downloads: "+ex.Message);}
        finally{_downloadsReading=false;}
    }
    static ImageSource DownloadCompleteIcon()
    {
        var drawing=new DrawingGroup();drawing.Children.Add(new GeometryDrawing(PanelControls.Chrome,null,new EllipseGeometry(new Point(15,15),15,15)));
        drawing.Children.Add(new GeometryDrawing(null,new Pen(Accent,2),Geometry.Parse("M 8 15 L 13 20 L 23 10")));drawing.Freeze();var image=new DrawingImage(drawing);image.Freeze();return image;
    }
    void ExtrasTick(){_=RefreshHardwareAsync();_=RefreshDownloadsAsync();}
}
