using System.Windows;
using System.Windows.Threading;

namespace IslandBar;

sealed class DragDwell
{
    string _key="";
    DateTime _since;
    bool _activated;
    public bool ShouldPreview(string key,DateTime now)
    {
        if(key!=_key){_key=key;_since=now;_activated=false;return false;}
        if(string.IsNullOrEmpty(key)||_activated||now-_since<TimeSpan.FromMilliseconds(650))return false;
        _activated=true;return true;
    }
    public void Reset(){_key="";_activated=false;}
}

sealed partial class IslandWindow
{
    readonly DispatcherTimer _dragTimer=new() {Interval=TimeSpan.FromMilliseconds(80)};
    bool _fileDragging;
    readonly DragDwell _dragDwell=new();
    DateTime _dragStarted;
    int _dragActivationCount;
    void InitializeFileDrag()
    {
        _frame.AllowDrop=true;
        _frame.PreviewDragEnter+=FileDragOver;
        _frame.PreviewDragOver+=FileDragOver;
        _frame.PreviewDrop+=(_,e)=>{if(e.Data.GetDataPresent("IslandBar.AppKey"))return;e.Effects=DragDropEffects.None;e.Handled=true;StopFileDrag(true);};
        _frame.DragLeave+=(_,_)=>Dispatcher.BeginInvoke(()=>{if(_fileDragging&&Native.GetCursorPos(out var p)&&!InsideCapsule(p.X,p.Y))StopFileDrag(true);},DispatcherPriority.Input);
        _dragTimer.Tick+=(_,_)=>FileDragTick();
    }
    void FileDragOver(object sender,DragEventArgs e)
    {
        if(e.Data.GetDataPresent("IslandBar.AppKey"))return;
        // The island previews a window; it never accepts, copies, opens or sends a file.
        e.Effects=DragDropEffects.None;e.Handled=true;
        if(!e.Data.GetDataPresent(DataFormats.FileDrop)){StopFileDrag(false);return;}
        if(_fileDragging)return;
        _fileDragging=true;_dragDwell.Reset();_dragStarted=DateTime.UtcNow;
        CloseFeature();CloseMenus();_dragTimer.Start();WriteStatus();
    }
    static bool PointInside(FrameworkElement element,int x,int y)
    {
        if(!element.IsVisible||element.ActualWidth<=0||element.ActualHeight<=0)return false;
        var a=element.PointToScreen(new Point(0,0));var b=element.PointToScreen(new Point(element.ActualWidth,element.ActualHeight));return new Rect(a,b).Contains(x,y);
    }
    void FileDragTick()
    {
        if(_closing||Native.GetAsyncKeyState(1)>=0){StopFileDrag(true);return;}
        if(!Native.GetCursorPos(out var point)||!InsideCapsule(point.X,point.Y)){StopFileDrag(true);return;}
        if(!_expanded){if(DateTime.UtcNow-_dragStarted>TimeSpan.FromMilliseconds(320))Expand();return;}
        var tile=PointInside(_scroll,point.X,point.Y)?_tiles.Values.FirstOrDefault(t=>PointInside(t.Button,point.X,point.Y)):null;
        if(tile is null||tile.App.Windows.All(w=>!Native.IsWindow(w))){_dragDwell.Reset();return;}
        if(!_dragDwell.ShouldPreview(tile.App.Key,DateTime.UtcNow))return;
        nint window=tile.App.Windows.FirstOrDefault(Native.IsWindow);if(window==0)return;
        Native.PreviewWindow(window);_dragActivationCount++;Program.Log("file drag preview: "+tile.App.Name);WriteStatus();
    }
    void StopFileDrag(bool collapse)
    {
        _dragTimer.Stop();_fileDragging=false;_dragDwell.Reset();
        if(collapse&&!_closing)Collapse();
    }
}
