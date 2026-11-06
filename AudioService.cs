using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace IslandBar;

sealed record OutputDevice(string Id,string Name,bool Default);
sealed record AudioState(string Name,int Volume,bool Muted,OutputDevice[] Devices);
static class AudioService
{
    public static AudioState Read()
    {
        using var enumerator=new MMDeviceEnumerator();using var current=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
        var devices=new List<OutputDevice>();
        foreach(var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active))using(device)devices.Add(new(device.ID,device.FriendlyName,device.ID==current.ID));
        return new(current.FriendlyName,(int)Math.Round(current.AudioEndpointVolume.MasterVolumeLevelScalar*100),current.AudioEndpointVolume.Mute,devices.ToArray());
    }
    public static void SetVolume(int value)
    {
        using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
        device.AudioEndpointVolume.MasterVolumeLevelScalar=Math.Clamp(value,0,100)/100f;
    }
    public static void ToggleMute()
    {
        using var enumerator=new MMDeviceEnumerator();using var device=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
        device.AudioEndpointVolume.Mute=!device.AudioEndpointVolume.Mute;
    }
    // Windows' policy client is also used by native endpoint-switching utilities.
    public static void SelectDevice(string id)
    {
        var client=(IPolicyConfig)new PolicyConfigClient();
        try {Marshal.ThrowExceptionForHR(client.SetDefaultEndpoint(id,0));Marshal.ThrowExceptionForHR(client.SetDefaultEndpoint(id,1));}
        finally{Marshal.ReleaseComObject(client);}
    }
    public static void VerifyPolicy()
    {
        using var enumerator=new MMDeviceEnumerator();var client=(IPolicyConfig)new PolicyConfigClient();
        try{foreach(var role in new[]{Role.Console,Role.Multimedia}){using var device=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,role);Marshal.ThrowExceptionForHR(client.SetDefaultEndpoint(device.ID,(int)role));}}
        finally{Marshal.ReleaseComObject(client);}
    }
    [ComImport,Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigClient {}
    [ComImport,Guid("f8679f50-850a-41cf-9c72-430f290290c8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig]int GetMixFormat(nint id,nint format);
        [PreserveSig]int GetDeviceFormat(nint id,int def,nint format);
        [PreserveSig]int ResetDeviceFormat(nint id);
        [PreserveSig]int SetDeviceFormat(nint id,nint endpoint,nint mix);
        [PreserveSig]int GetProcessingPeriod(nint id,int def,nint period,nint min);
        [PreserveSig]int SetProcessingPeriod(nint id,nint period);
        [PreserveSig]int GetShareMode(nint id,nint mode);
        [PreserveSig]int SetShareMode(nint id,nint mode);
        [PreserveSig]int GetPropertyValue(nint id,nint key,nint value);
        [PreserveSig]int SetPropertyValue(nint id,nint key,nint value);
        [PreserveSig]int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)]string id,int role);
        [PreserveSig]int SetEndpointVisibility(nint id,int visible);
    }
}
