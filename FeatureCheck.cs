using System.Text.Json;
using NAudio.CoreAudioApi;
using System.Windows;
using System.Windows.Interop;

namespace IslandBar;

static class FeatureCheck
{
    public static async Task RunAsync(bool exercise)
    {
        var checks=new Dictionary<string,bool>();var music=new MusicService();await music.RefreshAsync();
        var track=music.Current;var audio=AudioService.Read();
        checks["AudioDevicesAvailable"]=audio.Devices.Length>0;
        object? nativeLayoutGeometry=null;
        if(exercise){
            using var enumerator=new MMDeviceEnumerator();using var endpoint=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);float original=endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
            try{int target=audio.Volume>=2?audio.Volume-1:audio.Volume+1;AudioService.SetVolume(target);checks["VolumeWriteRead"]=AudioService.Read().Volume==target;}
            finally{endpoint.AudioEndpointVolume.MasterVolumeLevelScalar=original;}
            checks["VolumeRestored"]=Math.Abs(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar-original)<.0001;
            AudioService.VerifyPolicy();checks["AudioDevicePolicy"]=true;
            if(track?.CanToggle==true){
                bool toggled=await music.CommandAsync("toggle");
                try{for(int i=0;i<10;i++){await Task.Delay(100);await music.RefreshAsync();if(music.Current?.Playing!=track.Playing)break;}checks["MusicToggle"]=toggled&&music.Current?.Playing!=track.Playing;}
                finally{if(music.Current?.Key==track.Key&&music.Current.Playing!=track.Playing){await music.CommandAsync("toggle");for(int i=0;i<10;i++){await Task.Delay(100);await music.RefreshAsync();if(music.Current?.Playing==track.Playing)break;}}}
                checks["MusicPlaybackRestored"]=music.Current?.Playing==track.Playing;
            }
        }
        var entries=SearchWindow.BuildIndex();checks["SearchIndex"]=entries.Count>5;
        var area=new Rect(12,12,1416,960);
        foreach(int layout in new[]{2,3,4,5}){
            var boxes=WindowLayouts.Calculate(area,layout);
            checks["Layout"+layout]=boxes.All(area.Contains)&&boxes.SelectMany((a,i)=>boxes.Skip(i+1).Select(b=>!a.IntersectsWith(b))).All(x=>x);
        }
        if(exercise){
            var first=new Window {Title="IslandBar layout check A",Width=300,Height=200,Left=20,Top=20,WindowStyle=WindowStyle.None,ShowInTaskbar=false};
            var second=new Window {Title="IslandBar layout check B",Width=300,Height=200,Left=30,Top=30,WindowStyle=WindowStyle.None,ShowInTaskbar=false};
            try{
                nint a=new WindowInteropHelper(first).EnsureHandle(),b=new WindowInteropHelper(second).EnsureHandle();
                WindowLayouts.Apply([a,b],2);
                var ra=WindowLayouts.VisibleBounds(a);var rb=WindowLayouts.VisibleBounds(b);
                checks["NativeWindowLayout"]=ra.Right<rb.Left&&ra.Bottom>ra.Top&&rb.Bottom>rb.Top;
                nativeLayoutGeometry=new {A=new {ra.Left,ra.Top,ra.Right,ra.Bottom},B=new {rb.Left,rb.Top,rb.Right,rb.Bottom}};
            }finally{first.Close();second.Close();}
        }
        var result=new {Success=checks.Values.All(v=>v),Checks=checks,NativeLayoutGeometry=nativeLayoutGeometry,Music=track is null?null:new {track.Title,track.Artist,track.Playing,track.CanToggle,track.CanPrevious,track.CanNext,track.CanSeek,track.Duration,CoverWidth=track.Cover?.PixelWidth},Audio=audio,SearchEntries=entries.Count,Packaged=Native.IsPackaged};
        File.WriteAllText(Path.Combine(Program.Folder,"feature-check.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions {WriteIndented=true}));
    }
}
