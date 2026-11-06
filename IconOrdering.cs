using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar;
sealed partial class IslandWindow
{
    bool _iconReordering;
    Point _iconDown;
    void ConfigureIconOrdering(AppTile tile)
    {
        var button=tile.Button;button.AllowDrop=true;
        button.PreviewMouseLeftButtonDown+=(_,e)=>_iconDown=e.GetPosition(button);
        button.PreviewMouseMove+=(_,e)=>{
            if(e.LeftButton!=MouseButtonState.Pressed||_iconReordering)return;var point=e.GetPosition(button);
            if(Math.Abs(point.X-_iconDown.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-_iconDown.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            _iconReordering=true;ClosePreview();button.ReleaseMouseCapture();e.Handled=true;try{DragDrop.DoDragDrop(button,new DataObject("IslandBar.AppKey",tile.App.Key),DragDropEffects.Move);}finally{_iconReordering=false;}
        };
        button.DragOver+=(_,e)=>{if(e.Data.GetDataPresent("IslandBar.AppKey")){e.Effects=DragDropEffects.Move;e.Handled=true;}};
        button.Drop+=(_,e)=>{
            if(e.Data.GetData("IslandBar.AppKey") is not string key)return;e.Effects=DragDropEffects.Move;e.Handled=true;
            var keys=OrderedKeys();int index=keys.IndexOf(tile.App.Key);keys.Remove(key);keys.Insert(Math.Clamp(index,0,keys.Count),key);SaveIconOrder(keys);
        };
    }
    List<string> OrderedKeys()=>_apps.Children.OfType<System.Windows.Controls.Button>().Select(b=>_tiles.Values.First(t=>ReferenceEquals(t.Button,b)).App.Key).ToList();
    void MoveIcon(AppTile tile,int delta){var keys=OrderedKeys();int index=keys.IndexOf(tile.App.Key),target=index+delta;if(index<0||target<0||target>=keys.Count)return;(keys[index],keys[target])=(keys[target],keys[index]);SaveIconOrder(keys);}
    void SaveIconOrder(List<string> keys)
    {
        DockSettings.SaveOrder(keys);var positions=_tiles.Values.ToDictionary(t=>t.App.Key,t=>t.Button.TranslatePoint(new Point(0,0),_apps).X);
        for(int i=0;i<keys.Count;i++)if(_tiles.TryGetValue(keys[i],out var tile)){int index=_apps.Children.IndexOf(tile.Button);if(index!=i){if(index>=0)_apps.Children.RemoveAt(index);_apps.Children.Insert(i,tile.Button);}}
        _apps.UpdateLayout();
        if(ChromeMotion.Enabled)foreach(var tile in _tiles.Values)if(tile.Button.RenderTransform is TransformGroup group&&group.Children.OfType<TranslateTransform>().FirstOrDefault() is {} shift){double delta=positions[tile.App.Key]-tile.Button.TranslatePoint(new Point(0,0),_apps).X;shift.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(delta,0,TimeSpan.FromMilliseconds(180)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});}
        _=RefreshAppsAsync();
    }
}
