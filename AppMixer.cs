using NAudio.CoreAudioApi;
using System.Diagnostics;

namespace IslandBar;
sealed record MixerApp(string Key,string Name,int Volume,bool Muted);
static class AppMixer
{
    static string Key(AudioSessionControl session)
    {
        if(session.IsSystemSoundsSession)return "system";
        try{using var process=Process.GetProcessById((int)session.GetProcessID);return process.ProcessName;}catch{return "pid:"+session.GetProcessID;}
    }
    static void Each(Action<string,AudioSessionControl> action)
    {
        using var enumerator=new MMDeviceEnumerator();
        foreach(var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))using(device){var sessions=device.AudioSessionManager.Sessions;for(int i=0;i<sessions.Count;i++){var session=sessions[i];try{if(session.State!=NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)action(Key(session),session);}catch{}finally{session.Dispose();}}}
    }
    public static MixerApp[] Read()
    {
        var result=new Dictionary<string,List<(float Volume,bool Muted)>>(StringComparer.OrdinalIgnoreCase);
        Each((key,s)=>{if(!result.TryGetValue(key,out var entries))result[key]=entries=[];entries.Add((s.SimpleAudioVolume.Volume,s.SimpleAudioVolume.Mute));});
        string Name(string key)=>key.ToLowerInvariant() switch{"system"=>"Системные звуки","chrome"=>"Google Chrome","msedge"=>"Microsoft Edge","steam" or "steamwebhelper"=>"Steam","browser"=>"Яндекс Браузер",_=>key};
        return result.Select(p=>new MixerApp(p.Key,Name(p.Key),(int)Math.Round(p.Value.Average(v=>v.Volume)*100),p.Value.All(v=>v.Muted))).OrderBy(a=>a.Name).ToArray();
    }
    public static bool SetVolume(string key,int volume)
    {
        bool changed=false;
        Each((name,session)=>{
            if(!name.Equals(key,StringComparison.OrdinalIgnoreCase))return;
            session.SimpleAudioVolume.Volume=Math.Clamp(volume,0,100)/100f;
            changed=true;
        });
        return changed;
    }
    public static void SetMute(string key,bool muted)=>Each((name,s)=>{if(name.Equals(key,StringComparison.OrdinalIgnoreCase))s.SimpleAudioVolume.Mute=muted;});
    public static bool Probe()
    {
        int attempted=0,passed=0;Each((key,s)=>{float original=s.SimpleAudioVolume.Volume;attempted++;try{float test=original>.01f?original-.005f:original+.005f;s.SimpleAudioVolume.Volume=test;if(Math.Abs(s.SimpleAudioVolume.Volume-test)<.0001f)passed++;}finally{s.SimpleAudioVolume.Volume=original;}});return attempted>0&&attempted==passed;
    }
}
