using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandBar;

sealed partial class IslandWindow
{
    static readonly Brush Accent=PanelControls.Silver;
    static readonly Brush Secondary=PanelControls.Secondary;
    readonly MusicService _musicService=new();
    readonly Popup _featurePopup=new() {AllowsTransparency=true,StaysOpen=true,Placement=PlacementMode.Top};
    HwndSource? _featureSource;
    readonly Image _compactCover=new() {Width=30,Height=30,Stretch=Stretch.UniformToFill};
    readonly TextBlock _compactTitle=Label("Музыка",11,FontWeights.SemiBold);
    readonly TextBlock _compactArtist=Label("Spotify · Яндекс.Музыка",10);
    readonly StackPanel _toastContent=new() {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,12,0)};
    readonly Image _toastCover=new() {Width=30,Height=30,Stretch=Stretch.UniformToFill,Margin=new Thickness(0,0,10,0)};
    readonly TextBlock _toastTitle=Label("",11,FontWeights.SemiBold);
    Button? _toastButton;
    DateTime _toastUntil;
    string? _lastMusicKey;
    bool _musicRefresh,_panelUpdating,_featureClosing;
    string _featureKind="";
    readonly Dictionary<string,FrameworkElement> _featureTriggers=[];
    TextBlock? _panelError,_musicTitle,_musicArtist,_musicTime,_musicPlay;
    Button? _musicPreviousButton,_musicPlayButton,_musicNextButton;
    Image? _musicCover;
    Slider? _musicPosition,_volumeSlider;
    Slider? _musicVolume;
    TextBlock? _musicVolumeText,_musicVolumeLabel;
    string? _musicVolumeKey;
    TextBlock? _volumeNumber,_volumeName,_muteLabel;
    StackPanel? _outputs;
    string _outputKey="";
    bool ToastVisible=>!_expanded&&DateTime.UtcNow<_toastUntil;
    double TargetWidth=>_expanded?_openWidth:ToastVisible?288:96;
    double TargetHeight=>_expanded?56:ToastVisible?48:32;

    static TextBlock LeftLabel(string text,double size=12,Brush? color=null)
    {
        var label=Label(text,size);label.HorizontalAlignment=HorizontalAlignment.Left;label.TextAlignment=TextAlignment.Left;
        label.Foreground=color??Brushes.White;label.TextTrimming=TextTrimming.CharacterEllipsis;return label;
    }
    StackPanel CardColumn(double width=324)=>new() {Width=Math.Min(width,Math.Max(100,DockDisplayArea().Width-52))};
    static TextBlock CardTitle(string text)
    {
        var title=LeftLabel(text,19);
        title.FontWeight=FontWeights.SemiBold;
        title.Margin=new Thickness(0,0,0,13);
        return title;
    }
    static Border CoverFrame(Image image,double radius=10)
    {
        image.Clip=new RectangleGeometry(new Rect(0,0,image.Width,image.Height),radius,radius);RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        var placeholder=Label("♪",image.Width*.5);placeholder.Foreground=Secondary;
        var layers=new Grid();layers.Children.Add(placeholder);layers.Children.Add(image);
        return new() {Child=layers,CornerRadius=new CornerRadius(radius),Background=PanelControls.Tile,ClipToBounds=true};
    }
    void InitializeFeatures()
    {
        var search=MakeButton(Glyph("\uE721",17),"Поиск приложений · Ctrl+Alt+Пробел",32);
        search.Click+=(_,_)=>OpenSearch();_dock.Children.Insert(1,search);
        var layout=MakeButton(Glyph("\uECA5",17),"Раскладка окон",32);
        _featureTriggers["layouts"]=layout;
        layout.Click+=(_,_)=>OpenLayouts();_dock.Children.Insert(2,layout);
        var texts=new StackPanel {VerticalAlignment=VerticalAlignment.Center,Width=106,Margin=new Thickness(8,0,0,0)};
        foreach(var text in new[]{_compactTitle,_compactArtist}){text.HorizontalAlignment=HorizontalAlignment.Left;text.TextAlignment=TextAlignment.Left;text.TextTrimming=TextTrimming.CharacterEllipsis;}
        _compactArtist.Foreground=Secondary;texts.Children.Add(_compactTitle);texts.Children.Add(_compactArtist);
        var visual=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(6,0,6,0)};
        visual.Children.Add(CoverFrame(_compactCover,6));visual.Children.Add(texts);
        var music=MakeButton(visual,"Музыкальный плеер",158);music.Click+=(_,_)=>OpenMusic();
        _featureTriggers["music"]=music;
        _dock.Children.Insert(_dock.Children.IndexOf(_scroll)+1,music);
        InitializeExtras();InitializeFileDrag();InitializeConvenience();
        _toastTitle.TextTrimming=TextTrimming.CharacterEllipsis;_toastTitle.Width=222;
        _toastContent.Children.Add(_toastCover);_toastContent.Children.Add(_toastTitle);
        _toastButton=MakeButton(_toastContent,"Открыть музыкальный плеер",286,46);
        _toastButton.Visibility=Visibility.Hidden;_toastButton.Click+=(_,_)=>{Expand();if(_toastAction is {} action)action();else OpenMusic();};_surface.Children.Add(_toastButton);
        _featurePopup.PlacementTarget=(UIElement)Content;
        _featurePopup.Placement=PlacementMode.Custom;
        _featurePopup.CustomPopupPlacementCallback=(size,target,offset)=>[
            new CustomPopupPlacement(new Point((target.Width-size.Width)/2,target.Height-_frame.Margin.Bottom-TargetHeight-size.Height),PopupPrimaryAxis.Horizontal)
        ];
        _featurePopup.Opened+=(_,_)=>{
            if(_featurePopup.Child is Visual visual&&PresentationSource.FromVisual(visual) is HwndSource source&&source!=_featureSource){
                UnhookFeatureFocus();
                _featureSource=source;
                source.AddHook(KeepForeground);
            }
            SyncOutsideTracking();WriteStatus();
        };
        _featurePopup.Closed+=(_,_)=>{if(!_featurePopup.IsOpen)UnhookFeatureFocus();SyncOutsideTracking();WriteStatus();};
    }
    void UnhookFeatureFocus()
    {
        if(_featureSource is {IsDisposed:false} source)source.RemoveHook(KeepForeground);
        _featureSource=null;
    }
    void OpenCard(string kind,StackPanel content)
    {
        if(_featurePopup.IsOpen&&_featureKind==kind){CloseFeature();return;}
        CloseStartMenu(immediate:true);ClosePreview();CloseMenus();
        if(_featureTriggers.TryGetValue(kind,out var trigger)&&trigger.ToolTip is ToolTip tip)tip.IsOpen=false;
        _featureClosing=false;
        _panelError=LeftLabel("",11,Secondary);_panelError.TextWrapping=TextWrapping.Wrap;_panelError.Margin=new Thickness(0,8,0,0);_panelError.Visibility=Visibility.Collapsed;
        content.Children.Add(_panelError);
        var border=PanelControls.Frame(content,new Thickness(20));
        border.Margin=new Thickness(0,0,0,10);
        System.Windows.Documents.TextElement.SetFontFamily(border,new FontFamily("Segoe UI"));
        var container=new Grid();container.Children.Add(border);
        _featurePopup.Child=container;_featureKind=kind;_featurePopup.IsOpen=true;
        UpdatePanelMarker();
        ChromeMotion.Enter(border);
        int step=0;
        foreach(var group in content.Children.OfType<FrameworkElement>().Where(element=>element.Visibility==Visibility.Visible).Take(7))
            ChromeMotion.Reveal(group,step++);
        SyncOutsideTracking();WriteStatus();
    }
    void CloseFeature(bool immediate=false)
    {
        _featureKind="";
        UpdatePanelMarker();
        if(!_featurePopup.IsOpen)return;
        if(immediate||_closing||!ChromeMotion.Enabled){
            FinishClosingFeature();
            return;
        }
        if(_featureClosing)return;
        if(_featurePopup.Child is not Grid {Children.Count:>0} container||container.Children[0] is not Border border){
            FinishClosingFeature();
            return;
        }
        _featureClosing=true;
        container.IsHitTestVisible=false;
        ChromeMotion.Exit(border,()=>{
            if(ReferenceEquals(_featurePopup.Child,container))FinishClosingFeature();
        });
    }
    void FinishClosingFeature()
    {
        _featureClosing=false;
        _featurePopup.IsOpen=false;
        _featurePopup.Child=null;
        SyncOutsideTracking();WriteStatus();
    }
    bool InsideFeature(int x,int y)
    {
        if(!_featurePopup.IsOpen||_featureClosing||_featurePopup.Child is not Visual visual)return false;
        nint window=(PresentationSource.FromVisual(visual) as HwndSource)?.Handle??0;
        return window!=0&&Native.GetWindowRect(window,out var r)&&x>=r.Left&&x<r.Right&&y>=r.Top&&y<r.Bottom;
    }
    void PanelError(string text){if(_panelError is not null){_panelError.Text=text;_panelError.Visibility=Visibility.Visible;}}
    void SetToastAction(string name,Action action)
    {
        _toastAction=action;if(_toastButton is null)return;
        System.Windows.Automation.AutomationProperties.SetName(_toastButton,name);
        if(_toastButton.ToolTip is ToolTip tip)tip.Content=name;
    }
    bool InsideFeatureTrigger(int x,int y)
    {
        if(!_featurePopup.IsOpen)return false;
        foreach(var button in _featureTriggers.Values){if(!button.IsVisible)continue;var top=button.PointToScreen(new Point(0,0));var bottom=button.PointToScreen(new Point(button.ActualWidth,button.ActualHeight));if(new Rect(top,bottom).Contains(x,y))return true;}
        return false;
    }
    void OpenMusic()
    {
        var column=CardColumn();column.Children.Add(CardTitle("Музыка"));
        var row=new DockPanel();_musicCover=new Image {Width=70,Height=70,Stretch=Stretch.UniformToFill};
        var art=CoverFrame(_musicCover,12);art.Margin=new Thickness(0,0,14,0);DockPanel.SetDock(art,Dock.Left);row.Children.Add(art);
        var texts=new StackPanel {VerticalAlignment=VerticalAlignment.Center};_musicTitle=LeftLabel("Нет активного трека",16);_musicTitle.FontWeight=FontWeights.SemiBold;
        _musicArtist=LeftLabel("Выбери приложение ниже",12,Secondary);_musicArtist.Margin=new Thickness(0,5,0,0);texts.Children.Add(_musicTitle);texts.Children.Add(_musicArtist);row.Children.Add(texts);column.Children.Add(row);
        _musicPosition=new Slider {Minimum=0,Maximum=1,Value=0,Height=24,Margin=new Thickness(0,16,0,0),IsMoveToPointEnabled=true,Foreground=Accent};
        _musicPosition.Template=PanelControls.Slider;System.Windows.Automation.AutomationProperties.SetName(_musicPosition,"Позиция трека");
        _musicPosition.PreviewMouseLeftButtonUp+=async(_,_)=>{if(!_panelUpdating)await MusicCommand("seek",_musicPosition.Value);};
        _musicPosition.ValueChanged+=async(_,_)=>{if(!_panelUpdating&&_musicPosition.IsKeyboardFocusWithin&&!_musicPosition.IsMouseCaptureWithin)await MusicCommand("seek",_musicPosition.Value);};
        column.Children.Add(_musicPosition);_musicTime=LeftLabel("—",10,Secondary);column.Children.Add(_musicTime);
        var controls=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,10,0,4)};
        var previous=PanelControls.Button(Glyph("\uE892",20),"Предыдущий трек",44,true);previous.Width=52;previous.Click+=async(_,_)=>await MusicCommand("previous");controls.Children.Add(previous);
        _musicPlay=Glyph("\uE768",23);var play=PanelControls.Button(_musicPlay,"Пауза или воспроизведение",44,true);play.Width=60;play.Margin=new Thickness(6,0,6,0);PanelControls.FinishTile(play);play.Background=PanelControls.Chrome;play.Click+=async(_,_)=>await MusicCommand("toggle");controls.Children.Add(play);
        var next=PanelControls.Button(Glyph("\uE893",20),"Следующий трек",44,true);next.Width=52;next.Click+=async(_,_)=>await MusicCommand("next");controls.Children.Add(next);column.Children.Add(controls);
        _musicPreviousButton=previous;_musicPlayButton=play;_musicNextButton=next;
        previous.ToolTip=PanelControls.Hint("Предыдущий трек");play.ToolTip=PanelControls.Hint("Пауза или воспроизведение");next.ToolTip=PanelControls.Hint("Следующий трек");
        _musicVolumeLabel=LeftLabel("Общая громкость Windows",11,Secondary);
        _musicVolumeLabel.Margin=new Thickness(0,4,0,0);
        column.Children.Add(_musicVolumeLabel);
        var volume=new DockPanel {Margin=new Thickness(0,0,0,5)};
        var speaker=Glyph("\uE767",16);speaker.Margin=new Thickness(0,0,8,0);
        DockPanel.SetDock(speaker,Dock.Left);volume.Children.Add(speaker);
        _musicVolumeText=LeftLabel("—",11,Secondary);_musicVolumeText.Width=38;
        DockPanel.SetDock(_musicVolumeText,Dock.Right);volume.Children.Add(_musicVolumeText);
        _musicVolume=new Slider {Minimum=0,Maximum=100,Height=25,IsMoveToPointEnabled=true,Template=PanelControls.Slider,Foreground=Accent,IsEnabled=false};
        System.Windows.Automation.AutomationProperties.SetName(_musicVolume,"Громкость музыкального приложения");
        _musicVolume.ValueChanged+=(_,_)=>{
            if(_panelUpdating||!_musicVolume.IsEnabled)return;
            int value=(int)Math.Round(_musicVolume.Value);
            try{
                if(_musicVolumeKey is null)AudioService.SetVolume(value);
                else if(!AppMixer.SetVolume(_musicVolumeKey,value)){
                    PanelError("Аудиосессия приложения завершилась.");
                    return;
                }
                _musicVolumeText.Text=$"{value}%";
            }
            catch(Exception ex){Program.Log("music volume: "+ex.Message);PanelError("Не удалось изменить громкость плеера.");}
        };
        volume.Children.Add(_musicVolume);column.Children.Add(volume);
        var apps=new System.Windows.Controls.Primitives.UniformGrid {Columns=2,Margin=new Thickness(-3,4,-3,0)};
        foreach(string provider in new[]{MusicService.Spotify,MusicService.YandexMusic}){
            var open=PanelControls.Button(provider,"Открыть "+provider,34,true);
            open.Margin=new Thickness(3,0,3,0);PanelControls.FinishTile(open);
            open.Click+=(_,_)=>LaunchMusic(provider);apps.Children.Add(open);
        }
        column.Children.Add(apps);
        OpenCard("music",column);UpdateMusicPanel();
    }
    void LaunchMusic(string provider)
    {
        bool spotify=provider==MusicService.Spotify;
        string path=spotify
            ?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Spotify","Spotify.exe")
            :Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","YandexMusic","Яндекс Музыка.exe");
        try{
            if(!File.Exists(path)){
                if(!spotify){PanelError("Яндекс.Музыка не найдена");return;}
                path="spotify:";
            }
            Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
            CloseFeature();Collapse();
        }
        catch(Exception ex){PanelError("Не удалось открыть "+provider);Program.Log("open music: "+ex.Message);}
    }
    async Task MusicCommand(string command,double position=0)
    {
        if(!await _musicService.CommandAsync(command,position))PanelError("Плеер пока не принимает эту команду");
        await Task.Delay(120);await RefreshMusicAsync();
    }
    async Task RefreshMusicAsync()
    {
        if(_musicRefresh||_closing)return;_musicRefresh=true;
        try {
            await _musicService.RefreshAsync();if(_closing)return;
            var track=_musicService.Current;
            _compactTitle.Text=track?.Title??"Музыка";
            _compactArtist.Text=track is null?"Spotify · Яндекс.Музыка":string.IsNullOrWhiteSpace(track.Artist)?track.Provider??"Музыка":track.Artist;
            _compactCover.Source=track?.Cover;
            if(track is not null&&track.Key!=_lastMusicKey){
                if(_lastMusicKey is not null)RememberIslandEvent("Трек · "+track.Title,OpenMusic);
                if(_featureKind=="music"&&_musicCover is not null&&_musicTitle is not null){
                    ChromeMotion.Reveal(_musicCover);ChromeMotion.Reveal(_musicTitle,1);
                }
                if(_preferences.Toasts&&_lastMusicKey is not null&&!_expanded&&!_fullscreen&&!_featurePopup.IsOpen){
                    _toastCover.Source=track.Cover;_toastTitle.Text=track.Title;SetToastAction("Открыть музыкальный плеер",OpenMusic);_toastUntil=DateTime.UtcNow.AddSeconds(4);BeginMotion();
                }
                _lastMusicKey=track.Key;
            }
            UpdateMusicPanel();
        }catch(Exception ex){Program.Log("music UI: "+ex);}
        finally{_musicRefresh=false;}
    }
    static string TrackTime(double seconds)=>TimeSpan.FromSeconds(Math.Max(0,seconds)).ToString(seconds>=3600?@"h\:mm\:ss":@"m\:ss");
    void UpdateMusicPanel()
    {
        if(_featureKind!="music"||_musicTitle is null)return;
        var track=_musicService.Current;_musicTitle.Text=track?.Title??"Нет активного трека";
        _musicArtist!.Text=track is null?"Выбери приложение ниже":string.Join(" · ",new[]{track.Provider,track.Artist}.Where(s=>!string.IsNullOrWhiteSpace(s)));
        _musicCover!.Source=track?.Cover;
        _musicPlay!.Text=track?.Playing==true?"\uE769":"\uE768";
        _musicPreviousButton!.IsEnabled=track?.CanPrevious==true;
        _musicPlayButton!.IsEnabled=track?.CanToggle==true;
        _musicNextButton!.IsEnabled=track?.CanNext==true;
        _panelUpdating=true;
        try{
            if(!_musicPosition!.IsMouseCaptureWithin){_musicPosition.Maximum=Math.Max(1,track?.Duration??1);_musicPosition.Value=track?.Position??0;}
            _musicPosition.IsEnabled=track?.CanSeek==true;
            UpdateMusicVolume(track);
        }
        finally{_panelUpdating=false;}
        _musicTime!.Text=track is {Duration:>0}?TrackTime(track.Position)+"  /  "+TrackTime(track.Duration):"—";
    }
    static string? MusicAudioKey(MusicTrack? track, MixerApp[] apps)
    {
        if(track is null)return null;
        string source=track.Source;
        string normalized=source.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)
            ?Path.GetFileNameWithoutExtension(source):source.Split('!')[0];
        var exact=apps.FirstOrDefault(app=>normalized.Equals(app.Key,StringComparison.OrdinalIgnoreCase));
        if(exact is not null&&exact.Key!="system")return exact.Key;
        if(track.Provider==MusicService.Spotify)
            return apps.FirstOrDefault(app=>app.Key.Equals("Spotify",StringComparison.OrdinalIgnoreCase))?.Key;
        if(track.Provider==MusicService.YandexMusic)
            return apps.FirstOrDefault(app=>app.Key.ToLowerInvariant() is
                "яндекс музыка" or "яндекс.музыка" or "yandexmusic" or "yandex music" or "yandex.music")?.Key;
        string prefix=normalized.Split('.')[0];
        return prefix.ToLowerInvariant() is "chrome" or "msedge" or "firefox" or "brave" or "opera" or "browser"
            ?apps.FirstOrDefault(app=>app.Key.Equals(prefix,StringComparison.OrdinalIgnoreCase))?.Key:null;
    }
    void UpdateMusicVolume(MusicTrack? track)
    {
        if(_musicVolume is null||_musicVolume.IsMouseCaptureWithin)return;
        _musicVolumeKey=null;
        try{
            var apps=track is null?[]:AppMixer.Read();
            _musicVolumeKey=MusicAudioKey(track,apps);
            var app=apps.FirstOrDefault(item=>item.Key==_musicVolumeKey);
            int volume=app?.Volume??AudioService.Read().Volume;
            string label=app is null?"Общая громкость Windows":"Громкость · "+(track?.Provider??app.Name);
            _musicVolumeLabel!.Text=label;
            System.Windows.Automation.AutomationProperties.SetName(_musicVolume,label);
            _musicVolumeText!.Text=$"{volume}%";
            _musicVolume.Value=volume;
            _musicVolume.IsEnabled=true;
        }
        catch(Exception ex){
            _musicVolume.IsEnabled=false;
            _musicVolumeLabel!.Text="Устройство вывода недоступно";
            _musicVolumeText!.Text="—";
            Program.Log("music volume state: "+ex.Message);
        }
    }
    void OpenSound()
    {
        var column=CardColumn();column.Children.Add(CardTitle("Звук"));
        _volumeName=LeftLabel("Устройство вывода",12,Secondary);_volumeName.Margin=new Thickness(0,0,0,8);column.Children.Add(_volumeName);
        var row=new DockPanel();_muteLabel=Glyph("\uE767",18);var mute=PanelControls.Button(_muteLabel,"Выключить или включить звук",36,true);mute.Width=40;mute.Margin=new Thickness(0,0,6,0);mute.Click+=(_,_)=>{try{AudioService.ToggleMute();UpdateSoundPanel();}catch(Exception ex){PanelError(ex.Message);}};DockPanel.SetDock(mute,Dock.Left);row.Children.Add(mute);
        _volumeNumber=Label("",12);_volumeNumber.Width=45;DockPanel.SetDock(_volumeNumber,Dock.Right);row.Children.Add(_volumeNumber);
        _volumeSlider=new Slider {Minimum=0,Maximum=100,Height=30,VerticalAlignment=VerticalAlignment.Center,IsMoveToPointEnabled=true,Foreground=Accent};
        _volumeSlider.Template=PanelControls.Slider;System.Windows.Automation.AutomationProperties.SetName(_volumeSlider,"Громкость");
        _volumeSlider.ValueChanged+=(_,_)=>{if(_panelUpdating)return;try{int value=(int)Math.Round(_volumeSlider.Value);AudioService.SetVolume(value);_volumeNumber.Text=$"{value}%";}catch(Exception ex){PanelError(ex.Message);}};row.Children.Add(_volumeSlider);column.Children.Add(row);
        var heading=LeftLabel("УСТРОЙСТВО ВЫВОДА",10,Secondary);heading.Margin=new Thickness(0,20,0,8);column.Children.Add(heading);_outputs=new StackPanel();column.Children.Add(PanelControls.Scroller(_outputs,180));
        var mixer=PanelControls.Button(LeftLabel("Микшер приложений",12),"Микшер приложений");mixer.Margin=new Thickness(0,8,0,0);PanelControls.FinishTile(mixer);mixer.Click+=(_,_)=>OpenMixer();column.Children.Add(mixer);
        var bluetooth=PanelControls.Button("Bluetooth","Управление Bluetooth");bluetooth.Margin=new Thickness(0,4,0,0);bluetooth.Click+=(_,_)=>OpenBluetooth();column.Children.Add(bluetooth);
        _outputKey="";OpenCard("sound",column);UpdateSoundPanel();
    }
    void UpdateSoundPanel()
    {
        if(_featureKind!="sound")return;
        try {
            var state=AudioService.Read();_volumeName!.Text=state.Name;_volumeNumber!.Text=$"{state.Volume}%";_muteLabel!.Text=state.Muted?"\uE74F":"\uE767";
            _panelUpdating=true;try{if(!_volumeSlider!.IsMouseCaptureWithin)_volumeSlider.Value=state.Volume;}finally{_panelUpdating=false;}
            string key=string.Join('|',state.Devices.Select(d=>d.Id+d.Default));if(key==_outputKey)return;_outputKey=key;_outputs!.Children.Clear();
            foreach(var device in state.Devices){
                var line=new DockPanel();
                var mark=LeftLabel(device.Default?"✓":"",12,Accent);mark.Width=20;line.Children.Add(mark);
                var name=LeftLabel(device.Name,12);name.TextWrapping=TextWrapping.Wrap;name.MaxHeight=32;line.Children.Add(name);
                var button=PanelControls.Button(line,device.Name,38);
                button.ToolTip=PanelControls.Hint(device.Name);
                if(device.Default)button.Background=PanelControls.Tile;
                button.Click+=(_,_)=>{try{AudioService.SelectDevice(device.Id);UpdateSoundPanel();}catch(Exception ex){PanelError("Не удалось переключить устройство");Program.Log("output: "+ex.Message);}};_outputs.Children.Add(button);
            }
        }catch(Exception ex){PanelError("Нет доступного звукового устройства");Program.Log("sound: "+ex.Message);}
    }
    void FeatureTick()
    {
        if(!ToastVisible&&_toastButton?.Visibility==Visibility.Visible&&!_rendering)BeginMotion();
        _=RefreshMusicAsync();UpdateSoundPanel();
        ExtrasTick();
        _=RefreshNetworkAsync();UpdateMixerPanel();
    }
}
