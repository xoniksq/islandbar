using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session=Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace IslandBar;

sealed record MusicTrack(string Title,string Artist,BitmapSource? Cover,bool Playing,double Position,double Duration,bool CanToggle,bool CanPrevious,bool CanNext,bool CanSeek,string Source)
{
    public string Key=>Source+"|"+Artist+"|"+Title;
    public string? Provider=>MusicService.ProviderName(Source);
}
sealed class MusicService
{
    public const string Spotify = "Spotify", YandexMusic = "Яндекс.Музыка";
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    Session? _shownSession;
    string _coverKey="";
    BitmapSource? _cover;
    bool _reading;
    public MusicTrack? Current {get;private set;}
    public static string? ProviderName(string source)
    {
        if(source.Contains("spotify",StringComparison.OrdinalIgnoreCase))return Spotify;
        return source.Contains("yandex",StringComparison.OrdinalIgnoreCase)&&source.Contains("music",StringComparison.OrdinalIgnoreCase)
            ?YandexMusic:null;
    }

    async Task<Session?> SessionAsync()
    {
        _manager??=await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return SelectSession(_manager.GetSessions(),_manager.GetCurrentSession(),Current?.Source);
    }

    static Session? SelectSession(IEnumerable<Session> sessions,Session? current,string? displayed)
    {
        return sessions.Where(s=>ProviderName(s.SourceAppUserModelId) is not null)
            .OrderByDescending(s=>s.GetPlaybackInfo().PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            .ThenByDescending(s=>string.Equals(s.SourceAppUserModelId,current?.SourceAppUserModelId,StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(s=>string.Equals(s.SourceAppUserModelId,displayed,StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()??current;
    }
    public async Task RefreshAsync()
    {
        if(_reading)return;_reading=true;
        try{await ShowSessionAsync(await SessionAsync());}
        catch(Exception ex){Program.Log("music: "+ex.Message);_shownSession=null;Current=null;}
        finally{_reading=false;}
    }

    async Task ShowSessionAsync(Session? session)
    {
        if(session is null){_shownSession=null;Current=null;return;}
        var props=await session.TryGetMediaPropertiesAsync();
        if(string.IsNullOrWhiteSpace(props.Title)){_shownSession=null;Current=null;return;}
        string key=session.SourceAppUserModelId+"|"+props.Artist+"|"+props.Title;
        if(key!=_coverKey){
            _cover=null;_coverKey=key;
            if(props.Thumbnail is not null)try {
                using var stream=await props.Thumbnail.OpenReadAsync();
                if(stream.Size is >0 and <10000000){
                    using var reader=new DataReader(stream.GetInputStreamAt(0));await reader.LoadAsync((uint)stream.Size);
                    var bytes=new byte[stream.Size];reader.ReadBytes(bytes);
                    using var memory=new MemoryStream(bytes);var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=192;image.StreamSource=memory;image.EndInit();image.Freeze();_cover=image;
                }
            }catch(Exception ex){Program.Log("cover: "+ex.Message);}
        }
        var playback=session.GetPlaybackInfo();var timeline=session.GetTimelineProperties();
        bool playing=playback.PlaybackStatus==GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double duration=Math.Max(0,(timeline.EndTime-timeline.StartTime).TotalSeconds);
        double position=(timeline.Position-timeline.StartTime).TotalSeconds;
        if(playing&&timeline.LastUpdatedTime.Year>2000)position+=(DateTimeOffset.Now-timeline.LastUpdatedTime).TotalSeconds;
        var controls=playback.Controls;
        Current=new(props.Title,props.Artist,_cover,playing,Math.Clamp(position,0,duration),duration,controls.IsPlayPauseToggleEnabled,controls.IsPreviousEnabled,controls.IsNextEnabled,controls.IsPlaybackPositionEnabled&&duration>0,session.SourceAppUserModelId);
        _shownSession=session;
    }
    public async Task<bool> CommandAsync(string command,double position=0)
    {
        try {
            var session=_shownSession;if(session is null)return false;
            return command switch {
                "toggle"=>await session.TryTogglePlayPauseAsync(),
                "previous"=>await session.TrySkipPreviousAsync(),
                "next"=>await session.TrySkipNextAsync(),
                "seek"=>await session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(Math.Max(0,position)).Ticks),_=>false};
        }catch(Exception ex){Program.Log("music command: "+ex);return false;}
    }
}
