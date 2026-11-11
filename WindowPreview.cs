using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace IslandBar;
sealed class WindowPreview:Window
{
    [StructLayout(LayoutKind.Sequential)]struct Size {public int Width,Height;}
    [StructLayout(LayoutKind.Sequential)]struct Properties {public uint Flags;public Native.Rect Destination,Source;public byte Opacity;[MarshalAs(UnmanagedType.Bool)]public bool Visible;[MarshalAs(UnmanagedType.Bool)]public bool ClientOnly;}
    [DllImport("dwmapi.dll")]static extern int DwmRegisterThumbnail(nint destination,nint source,out nint thumbnail);
    [DllImport("dwmapi.dll")]static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")]static extern int DwmQueryThumbnailSourceSize(nint thumbnail,out Size size);
    [DllImport("dwmapi.dll")]static extern int DwmUpdateThumbnailProperties(nint thumbnail,ref Properties properties);
    [DllImport("gdi32.dll")]static extern nint CreateRoundRectRgn(int left,int top,int right,int bottom,int ellipseWidth,int ellipseHeight);
    [DllImport("gdi32.dll")]static extern int CombineRgn(nint destination,nint first,nint second,int mode);
    [DllImport("gdi32.dll")]static extern bool DeleteObject(nint region);
    [DllImport("user32.dll")]static extern int SetWindowRgn(nint window,nint region,bool redraw);
    const int CardWidth=212,CardHeight=162,CardStride=218,CardRadius=13;
    readonly List<(nint Handle,Properties Layout)> _thumbnails=[];
    long _revealStarted;
    double _previewDpi;
    public int ThumbnailCount=>_thumbnails.Count;
    public IReadOnlyList<nint> Windows { get; }
    public WindowPreview(nint[] windows,Action<nint> activate,Action<nint> requestClose,bool inspect)
    {
        Windows=windows;
        Title="IslandBar — окна";Width=windows.Length*CardStride+16;Height=CardHeight+16;Background=PanelControls.Surface;WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=inspect;ShowActivated=false;Topmost=true;
        var panel=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(8)};
        Content=panel;
        foreach(var source in windows){
            var header=new DockPanel {Height=29,VerticalAlignment=VerticalAlignment.Top};
            var cross=new System.Windows.Shapes.Path {
                Data=Geometry.Parse("M 1 1 L 9 9 M 9 1 L 1 9"),
                Stroke=PanelControls.Silver,StrokeThickness=1.4,
                StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round
            };
            var dismiss=PanelControls.Button(cross,"Закрыть окно",24,center:true);
            dismiss.Width=24;dismiss.Padding=new Thickness(0);dismiss.Margin=new Thickness(3,3,5,2);
            dismiss.Focusable=false;
            dismiss.Click+=(_,_)=>requestClose(source);
            DockPanel.SetDock(dismiss,Dock.Right);header.Children.Add(dismiss);
            var title=new TextBlock {Text=Native.Title(source),Foreground=Brushes.White,FontSize=12,
                TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(8,0,4,0),
                VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
            header.Children.Add(title);
            var activationArea=new Border {Background=Brushes.Transparent};
            activationArea.MouseLeftButtonUp+=(_,_)=>activate(source);
            var content=new Grid();
            content.Children.Add(activationArea);
            content.Children.Add(header);
            var cell=new Border {
                Width=CardWidth,Height=CardHeight,Margin=new Thickness(3,0,3,0),CornerRadius=new CornerRadius(CardRadius),
                Background=PanelControls.Shell,
                Cursor=Cursors.Hand,Child=content
            };
            cell.MouseEnter+=(_,_)=>cell.Background=PanelControls.Tile;
            cell.MouseLeave+=(_,_)=>cell.Background=PanelControls.Shell;
            int index=panel.Children.Count;
            title.Loaded+=(_,_)=>ChromeMotion.Reveal(title,index);
            panel.Children.Add(cell);
        }
        SourceInitialized+=(_,_)=>{if(!inspect){nint hwnd=new WindowInteropHelper(this).Handle;Native.SetWindowLong(hwnd,-20,Native.GetWindowLong(hwnd,-20)|0x08000080);}};
        Loaded+=(_,_)=>{
            nint destination=new WindowInteropHelper(this).Handle;double dpi=VisualTreeHelper.GetDpi(this).DpiScaleX;
            _previewDpi=dpi;
            ApplyCardRegion(destination,windows.Length,dpi);
            for(int i=0;i<windows.Length;i++)if(DwmRegisterThumbnail(destination,windows[i],out var thumb)==0){
                DwmQueryThumbnailSourceSize(thumb,out var size);double scale=Math.Min(198d/Math.Max(1,size.Width),122d/Math.Max(1,size.Height));double w=size.Width*scale,h=size.Height*scale;
                var p=new Properties {Flags=0x1D,Opacity=255,Visible=true,ClientOnly=false,Destination=new Native.Rect {Left=(int)((18+i*CardStride+(198-w)/2)*dpi),Top=(int)((40+(122-h)/2)*dpi),Right=(int)((18+i*CardStride+(198+w)/2)*dpi),Bottom=(int)((40+(122+h)/2)*dpi)}};
                _thumbnails.Add((thumb,p));
            }
            bool animate=ChromeMotion.Enabled&&_thumbnails.Count>0;
            PlaceThumbnails(animate?0:1);
            if(animate){_revealStarted=Stopwatch.GetTimestamp();CompositionTarget.Rendering+=RevealThumbnails;}
        };
        Closed+=(_,_)=>{
            CompositionTarget.Rendering-=RevealThumbnails;
            foreach(var thumb in _thumbnails)DwmUnregisterThumbnail(thumb.Handle);
            _thumbnails.Clear();
        };
    }
    static void ApplyCardRegion(nint window,int count,double dpi)
    {
        // Keep the HWND non-layered for the live thumbnails, and clip away
        // the space around and between the rounded cards in the native window.
        int Pixels(int dip)=>(int)Math.Round(dip*dpi);
        int diameter=Pixels(CardRadius*2);
        nint region=0;
        try{
            for(int i=0;i<count;i++){
                int left=11+i*CardStride;
                nint card=CreateRoundRectRgn(Pixels(left),Pixels(8),Pixels(left+CardWidth),Pixels(8+CardHeight),diameter,diameter);
                if(card==0)return;
                if(region==0){region=card;continue;}
                int result=CombineRgn(region,region,card,2); // RGN_OR
                DeleteObject(card);
                if(result==0)return;
            }
            if(region!=0&&SetWindowRgn(window,region,true)!=0)region=0; // Windows owns it on success.
        }
        finally{if(region!=0)DeleteObject(region);}
    }
    void RevealThumbnails(object? sender,EventArgs args)
    {
        double time=Math.Min(1,Stopwatch.GetElapsedTime(_revealStarted).TotalMilliseconds/210);
        PlaceThumbnails(1-Math.Pow(1-time,3));
        if(time>=1)CompositionTarget.Rendering-=RevealThumbnails;
    }
    void PlaceThumbnails(double progress)
    {
        int lift=(int)Math.Round(7*_previewDpi*(1-progress));
        foreach(var thumbnail in _thumbnails){
            var layout=thumbnail.Layout;
            layout.Opacity=(byte)Math.Round(255*progress);
            layout.Destination.Top+=lift;layout.Destination.Bottom+=lift;
            DwmUpdateThumbnailProperties(thumbnail.Handle,ref layout);
        }
    }
    public bool Contains(int x,int y)
    {
        nint handle=new WindowInteropHelper(this).Handle;
        return handle!=0&&Native.GetWindowRect(handle,out var rect)&&x>=rect.Left&&x<rect.Right&&y>=rect.Top&&y<rect.Bottom
            &&Native.WindowFromPoint(new Native.Point {X=x,Y=y})==handle;
    }
}

sealed partial class IslandWindow
{
    readonly DispatcherTimer _previewTimer=new(){Interval=TimeSpan.FromMilliseconds(80)};
    AppTile? _hoverTile;
    DateTime _hoverSince;
    DateTime _previewLeave;
    WindowPreview? _windowPreview;
    string _previewKey="";
    void InitializePreviews(){_previewTimer.Tick+=(_,_)=>PreviewTick();}
    void HoverPreview(AppTile tile){if(!_expanded||!_preferences.WindowPreviews)return;_hoverTile=tile;_hoverSince=DateTime.UtcNow;_previewTimer.Start();}
    void ClosePreview(){_previewKey="";_windowPreview?.Close();_windowPreview=null;}
    bool InsidePreview(int x,int y)=>_windowPreview?.Contains(x,y)==true;
    void PreviewTick()
    {
        if(_closing||!_expanded){ClosePreview();_previewTimer.Stop();return;}
        if(_fileDragging||_iconReordering||_featurePopup.IsOpen||_startMenu is not null||_openMenus.Count>0){ClosePreview();return;}
        if(!Native.GetCursorPos(out var cursor))return;
        if(_windowPreview is { } shown && shown.Contains(cursor.X,cursor.Y)){
            _previewLeave=DateTime.MinValue;
            var remaining=shown.Windows.Where(w=>Native.IsWindow(w)&&Native.IsWindowVisible(w)).ToArray();
            if(remaining.Length!=shown.Windows.Count){
                if(remaining.Length>0&&_hoverTile is { } tileWithPreview)ShowPreview(tileWithPreview,remaining,shown.Left);
                else ClosePreview();
            }
            return;
        }
        var hovered=PointInside(_scroll,cursor.X,cursor.Y)?_tiles.Values.FirstOrDefault(t=>PointInside(t.Button,cursor.X,cursor.Y)):null;
        if(hovered is null){if(_windowPreview is not null){if(_previewLeave==DateTime.MinValue)_previewLeave=DateTime.UtcNow;if(DateTime.UtcNow-_previewLeave<TimeSpan.FromMilliseconds(250))return;}ClosePreview();_hoverTile=null;return;}
        _previewLeave=DateTime.MinValue;
        if(!ReferenceEquals(hovered,_hoverTile)){ClosePreview();_hoverTile=hovered;_hoverSince=DateTime.UtcNow;return;}
        var tile=hovered;
        if(_previewKey==tile.App.Key||DateTime.UtcNow-_hoverSince<TimeSpan.FromMilliseconds(550))return;
        var windows=tile.App.Windows.Where(Native.IsWindow).Take(4).ToArray();if(windows.Length==0)return;
        ShowPreview(tile,windows);
    }
    void ShowPreview(AppTile tile,nint[] windows,double? previousLeft=null)
    {
        ClosePreview();_previewKey=tile.App.Key;
        var preview=new WindowPreview(windows,w=>{Collapse();Native.Activate(w);},
            w=>{if(Native.IsWindow(w)&&!Native.PostMessage(w,0x0010,0,0))Program.Log("preview: close request was rejected");},
            _preview||_inspect);_windowPreview=preview;
        double dpi=VisualTreeHelper.GetDpi(this).DpiScaleX;var anchor=tile.Button.PointToScreen(new Point(tile.Button.ActualWidth/2,0));
        preview.Left=Math.Clamp(previousLeft??anchor.X/dpi-preview.Width/2,8,Math.Max(8,SystemParameters.PrimaryScreenWidth-preview.Width-8));preview.Top=_hitRect.Top/dpi-preview.Height-10;
        preview.Closed+=(_,_)=>{if(ReferenceEquals(_windowPreview,preview))_windowPreview=null;};preview.Show();
    }
}
