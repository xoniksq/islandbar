using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace IslandBar;

sealed partial class IslandWindow
{
    nint _nativeTrayMenu;
    bool _trayContextOpening;
    DispatcherTimer? _trayMenuTimer;
    bool NativeTrayMenuOpen=>_nativeTrayMenu!=0&&Native.IsWindowVisible(_nativeTrayMenu);

    void TrackNativeTrayMenu(nint menu)
    {
        _nativeTrayMenu=menu;
        if(_trayMenuTimer is null){
            _trayMenuTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};
            _trayMenuTimer.Tick+=(_,_)=>{
                if(NativeTrayMenuOpen)return;
                _trayMenuTimer.Stop();
                _nativeTrayMenu=0;
                SyncOutsideTracking();WriteStatus();
            };
        }
        _trayMenuTimer.Start();
    }

    bool InsideNativeTrayMenu(int x,int y)
    {
        if(!NativeTrayMenuOpen)return false;
        if(Native.GetWindowRect(_nativeTrayMenu,out var rect)&&x>=rect.Left&&x<rect.Right&&y>=rect.Top&&y<rect.Bottom)
            return true;
        nint hit=Native.WindowFromPoint(new Native.Point {X=x,Y=y});
        if(hit==0||Native.ClassName(hit)!="#32768")return false;
        uint thread=Native.GetWindowThreadProcessId(hit,out _);
        return thread!=0&&thread==Native.GetWindowThreadProcessId(_nativeTrayMenu,out _);
    }

    async Task OpenTray()
    {
        if(_featureKind=="tray"&&_featurePopup.IsOpen){CloseFeature();return;}
        if(_trayOpening)return;
        var column=CardColumn(456);
        column.Children.Add(CardTitle("Скрытые значки"));
        var status=LeftLabel("Загружаю значки…",12,Secondary);column.Children.Add(status);
        var tiles=new UniformGrid {Columns=3,VerticalAlignment=VerticalAlignment.Top};
        var grid=PanelControls.Scroller(tiles,336);grid.Visibility=Visibility.Collapsed;
        column.Children.Add(grid);
        OpenCard("tray",column);
        var current=_featurePopup.Child;
        _trayOpening=true;
        try{
            var icons=await _trayFlyout.ReadAsync();
            if(_featureKind!="tray"||!ReferenceEquals(_featurePopup.Child,current))return;
            foreach(var icon in icons){
                string name=icon.Name.Split('\n','\r')[0];
                if(string.IsNullOrWhiteSpace(name))continue;
                var app=_tiles.Values.Select(tile=>tile.App)
                    .FirstOrDefault(item=>string.Equals(item.Name,name,StringComparison.CurrentCultureIgnoreCase)||
                        name.StartsWith(item.Name+" — ",StringComparison.CurrentCultureIgnoreCase));
                FrameworkElement symbol;
                if(app?.Icon is {} source){
                    var image=new Image {Source=source,Width=23,Height=23};
                    RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
                    symbol=image;
                }
                else if(char.IsLetter(name[0]))symbol=Label(new string(name.TakeWhile(char.IsLetter).Take(2).ToArray()).ToUpperInvariant(),13,FontWeights.SemiBold);
                else symbol=Glyph("\uE8FD",15);
                var badge=PanelControls.Well(symbol,new Thickness(0),10);
                badge.Width=34;badge.Height=34;badge.Background=PanelControls.Chrome;
                var label=Label(name,11);label.TextTrimming=TextTrimming.CharacterEllipsis;label.Margin=new Thickness(0,6,0,0);
                var content=new StackPanel();content.Children.Add(badge);content.Children.Add(label);
                var button=PanelControls.Button(content,name,76);button.Margin=new Thickness(3,0,3,8);
                button.Padding=new Thickness(8,0,8,0);PanelControls.FinishTile(button);
                if(icon.Name.Length>16)button.ToolTip=PanelControls.Hint(icon.Name);
                button.Click+=async(_,_)=>await OpenTrayIcon(icon);
                button.PreviewMouseRightButtonUp+=async(_,e)=>{
                    e.Handled=true;
                    var point=button.PointToScreen(e.GetPosition(button));
                    await OpenTrayContext(button,icon,app,new Native.Point {X=(int)point.X,Y=(int)point.Y});
                };
                tiles.Children.Add(button);
            }
            if(tiles.Children.Count==0)status.Text="Доступных значков нет";
            else{
                status.Visibility=Visibility.Collapsed;
                grid.Visibility=Visibility.Visible;
                int step=0;
                foreach(var tile in tiles.Children.OfType<Button>())ChromeMotion.Reveal(tile,step++);
            }
        }
        catch(Exception ex){
            Program.Log("tray icons: "+ex.Message);
            if(_featureKind=="tray"&&ReferenceEquals(_featurePopup.Child,current)){
                status.Text="Не удалось загрузить значки";status.Visibility=Visibility.Visible;
                grid.Visibility=Visibility.Collapsed;
            }
        }
        finally{_trayOpening=false;WriteStatus();}
    }

    async Task OpenTrayContext(Button tile,TrayIcon icon,DockApp? app,Native.Point anchor)
    {
        if(_trayOpening)return;
        var card=_featurePopup.Child;
        _trayOpening=true;
        _trayContextOpening=true;
        SyncOutsideTracking();
        try{
            var nativeMenu=await _trayFlyout.ShowContextMenuAsync(icon,anchor);
            if(nativeMenu is not null){
                Program.Log("tray context: opened application menu");
                if(!_closing)TrackNativeTrayMenu(nativeMenu.Window);
                return;
            }
        }
        catch(Exception ex){Program.Log("tray context: "+ex.Message);}
        finally{
            _trayOpening=false;_trayContextOpening=false;
            SyncOutsideTracking();WriteStatus();
        }
        if(_featureKind!="tray"||!ReferenceEquals(_featurePopup.Child,card))return;

        var menu=CreateMenu();
        menu.Placement=PlacementMode.Top;menu.PlacementTarget=tile;menu.VerticalOffset=-6;
        TrackMenu(menu);
        var open=new MenuItem {Header="Открыть"};
        open.Click+=async(_,_)=>await OpenTrayIcon(icon);
        menu.Items.Add(open);
        if(app is not null&&app.Windows.Any(Native.IsWindow)){
            var close=new MenuItem {Header="Закрыть окна приложения"};
            close.Click+=(_,_)=>{
                foreach(nint window in app.Windows.Where(Native.IsWindow))Native.PostMessage(window,0x0010,0,0);
                CloseFeature();
            };
            menu.Items.Add(close);
        }
        menu.IsOpen=true;
    }

    async Task OpenTrayIcon(TrayIcon icon)
    {
        if(_trayOpening)return;
        _trayOpening=true;
        try{
            if(await _trayFlyout.InvokeAsync(icon)){Collapse();return;}
            if(_featureKind=="tray")PanelError("Приложение не ответило на нажатие");
        }
        catch(Exception ex){
            Program.Log("tray icon: "+ex.Message);
            if(_featureKind=="tray")PanelError("Не удалось открыть приложение");
        }
        finally{_trayOpening=false;}
    }
}
