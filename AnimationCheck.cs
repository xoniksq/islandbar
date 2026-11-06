using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar;

sealed partial class IslandWindow
{
    bool _checking;
    readonly List<FrameSample> _samples=[];
    sealed record FrameSample(double Time,double Width,double Height,double Left,double Top,double WindowWidth,double WindowHeight);

    public async Task CheckAnimationAsync()
    {
        try {
            while(_refreshing)await Task.Delay(30);
            await RefreshAppsAsync();await Task.Delay(200);_checking=true;
            SaveSnapshot("closed.png");
            Expand();await Task.Delay(85);SaveSnapshot("opening.png");
            Collapse();await Task.Delay(55);Expand();
            var settling=Stopwatch.StartNew();
            while(_rendering&&settling.ElapsedMilliseconds<2000)await Task.Delay(20);
            SaveSnapshot("open.png");
            bool openSettled=!_rendering&&Math.Abs(_frame.Width-_openWidth)<.1;
            var openGeometry=new {Animating=_rendering,Target=_openWidth,Width=_frame.Width,Height=_frame.Height,SettledMs=settling.ElapsedMilliseconds};
            var buttons=_tiles.ToDictionary(t=>t.Key,t=>t.Value.Button);
            await RefreshAppsAsync();
            bool preserved=buttons.Where(t=>_tiles.ContainsKey(t.Key)).All(t=>ReferenceEquals(t.Value,_tiles[t.Key].Button));
            Collapse();await Task.Delay(750);SaveSnapshot("closed-after.png");_checking=false;
            bool stableWindow=_samples.All(s=>s.Left==Left&&s.Top==Top&&s.WindowWidth==Width&&s.WindowHeight==Height);
            bool bounded=_samples.All(s=>s.Width>=96&&s.Width<=Width-24&&s.Height>=32&&s.Height<=56);
            bool closedSettled=!_rendering&&_frame.Width==96&&_frame.Height==32;
            var gaps=_samples.Zip(_samples.Skip(1),(a,b)=>(b.Time-a.Time)*1000).Where(ms=>ms<200).Order().ToArray();
            var report=new {Success=openSettled&&stableWindow&&preserved&&bounded&&closedSettled,OpenSettled=openSettled,
                NativeWindowStable=stableWindow,ButtonsPreserved=preserved,GeometryInBounds=bounded,ClosedSettled=closedSettled,
                AnimationsEnabled=SystemParameters.ClientAreaAnimation,FrameSamples=_samples.Count,
                OpenGeometry=openGeometry,
                MedianFrameMs=gaps.Length>0?gaps[gaps.Length/2]:0,P95FrameMs=gaps.Length>0?gaps[(int)((gaps.Length-1)*.95)]:0,
                IconSources=_tiles.Values.Select(t=>new {t.App.Name,Pixels=(t.App.Icon as BitmapSource)?.PixelWidth}).ToArray()};
            File.WriteAllText(Path.Combine(Program.Folder,"animation-check.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            Program.Log("animation check: "+report.Success);
        }catch(Exception ex){Program.Log("animation check: "+ex);}
        finally{Close();}
    }

    void SaveSnapshot(string name)
    {
        UpdateLayout();var dpi=VisualTreeHelper.GetDpi(this);
        var image=new RenderTargetBitmap((int)Math.Ceiling(Width*dpi.DpiScaleX),(int)Math.Ceiling(Height*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        image.Render((Visual)Content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        var folder=Path.Combine(Program.Folder,"visual-check");Directory.CreateDirectory(folder);
        using var stream=File.Create(Path.Combine(folder,name));encoder.Save(stream);
    }
}
