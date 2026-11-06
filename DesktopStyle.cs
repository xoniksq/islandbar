using Microsoft.Win32;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandBar;

static class DesktopStyle
{
    sealed record SavedValue(string Path,string Name,bool Exists,int Kind,string Data);
    const string Personalize=@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",Dwm=@"Software\Microsoft\Windows\DWM",AccentPath=@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",Desktop=@"Control Panel\Desktop";
    static readonly (string Path,string Name)[] Values=[(Personalize,"AppsUseLightTheme"),(Personalize,"SystemUsesLightTheme"),(Personalize,"EnableTransparency"),(Personalize,"ColorPrevalence"),(Dwm,"AccentColor"),(Dwm,"ColorizationColor"),(Dwm,"ColorPrevalence"),(Dwm,"AutoColorization"),(AccentPath,"AccentColorMenu"),(AccentPath,"StartColorMenu"),(AccentPath,"AccentPalette"),(Desktop,"Wallpaper"),(Desktop,"WallpaperStyle"),(Desktop,"TileWallpaper")];
    static string Backup=>Path.Combine(Program.Folder,"appearance-before-island.json");
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SystemParametersInfo(uint action,uint param,string value,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern nint SendMessageTimeout(nint window,uint message,nint param,string value,uint flags,uint timeout,out nint result);
    static void Set(string path,string name,object value,RegistryValueKind kind=RegistryValueKind.DWord){using var key=Registry.CurrentUser.CreateSubKey(path);key.SetValue(name,value,kind);}
    public static void Apply()
    {
        if(!File.Exists(Backup)){
            var saved=Values.Select(pair=>{using var key=Registry.CurrentUser.OpenSubKey(pair.Path);var value=key?.GetValue(pair.Name);return new SavedValue(pair.Path,pair.Name,value is not null,value is null?0:(int)key!.GetValueKind(pair.Name),value is byte[] bytes?Convert.ToBase64String(bytes):Convert.ToString(value,CultureInfo.InvariantCulture)??"");}).ToArray();
            File.WriteAllText(Backup,JsonSerializer.Serialize(saved,new JsonSerializerOptions {WriteIndented=true}));
        }
        string wallpaper=Path.Combine(Program.Folder,"Graphite Blue.bmp");CreateWallpaper(wallpaper);
        Set(Personalize,"AppsUseLightTheme",0);Set(Personalize,"SystemUsesLightTheme",0);Set(Personalize,"EnableTransparency",1);Set(Personalize,"ColorPrevalence",0);
        Set(Dwm,"AccentColor",unchecked((int)0xFFFFC067));Set(Dwm,"ColorizationColor",unchecked((int)0xC467C0FF));Set(Dwm,"ColorPrevalence",0);Set(Dwm,"AutoColorization",0);
        Set(AccentPath,"AccentColorMenu",unchecked((int)0xFFFFC067));Set(AccentPath,"StartColorMenu",unchecked((int)0xFF241C14));
        byte[] palette=[180,226,255,0,143,211,255,0,103,192,255,0,58,158,227,0,35,113,168,0,23,74,112,0,16,44,68,0,0,120,215,0];Set(AccentPath,"AccentPalette",palette,RegistryValueKind.Binary);
        Set(Desktop,"WallpaperStyle","10",RegistryValueKind.String);Set(Desktop,"TileWallpaper","0",RegistryValueKind.String);
        if(!SystemParametersInfo(20,0,wallpaper,3))throw new InvalidOperationException("Не удалось применить фон Windows");
        bool engine=WallpaperEngineStyle.Apply(wallpaper);
        Notify();File.WriteAllText(Path.Combine(Program.Folder,"appearance-result.json"),JsonSerializer.Serialize(new {Success=true,Packaged=Native.IsPackaged,Wallpaper=wallpaper,WallpaperEngine=engine,Accent="Graphite / blue",Backup}));
    }
    public static void Restore()
    {
        if(!File.Exists(Backup))return;
        foreach(var saved in JsonSerializer.Deserialize<SavedValue[]>(File.ReadAllText(Backup))??[]){
            using var key=Registry.CurrentUser.CreateSubKey(saved.Path);
            if(!saved.Exists){key.DeleteValue(saved.Name,false);continue;}
            var kind=(RegistryValueKind)saved.Kind;object value=kind switch {RegistryValueKind.Binary=>Convert.FromBase64String(saved.Data),RegistryValueKind.DWord=>int.Parse(saved.Data,CultureInfo.InvariantCulture),RegistryValueKind.QWord=>long.Parse(saved.Data,CultureInfo.InvariantCulture),_=>saved.Data};key.SetValue(saved.Name,value,kind);
        }
        using var desktop=Registry.CurrentUser.OpenSubKey(Desktop);string wallpaper=desktop?.GetValue("Wallpaper") as string??"";
        SystemParametersInfo(20,0,wallpaper,3);WallpaperEngineStyle.Restore();Notify();
    }
    static void Notify(){SendMessageTimeout(0xffff,0x1A,0,"ImmersiveColorSet",2,2000,out _);SendMessageTimeout(0xffff,0x1A,0,"WindowsThemeElement",2,2000,out _);}
    static void CreateWallpaper(string path)
    {
        const int width=3840,height=2160;var visual=new DrawingVisual();
        using(var draw=visual.RenderOpen()){
            draw.DrawRectangle(new LinearGradientBrush(Color.FromRgb(16,20,28),Color.FromRgb(35,42,54),35),null,new Rect(0,0,width,height));
            var glow=new RadialGradientBrush(Color.FromArgb(36,48,142,214),Colors.Transparent) {Center=new Point(.64,.44),GradientOrigin=new Point(.64,.44),RadiusX=.7,RadiusY=.8};draw.DrawRectangle(glow,null,new Rect(0,0,width,height));
            for(int i=0;i<6;i++){
                var geometry=Geometry.Parse($"M {-600+i*110} 2200 C {500+i*100} {1850-i*42} {1400+i*160} {850-i*22} {4200+i*120} {250+i*60}");
                draw.DrawGeometry(null,new Pen(new SolidColorBrush(Color.FromArgb((byte)(i==0?24:11),128,178,218)),i==0?2:1),geometry);
            }
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new BmpBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(path);encoder.Save(output);
    }
}
