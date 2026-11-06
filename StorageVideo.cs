using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace IslandBar;

static class VideoMetadata
{
    public static string? FindProbe()
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) {
            string path = Path.Combine(folder.Trim('"'), "ffprobe.exe");
            if (Path.IsPathFullyQualified(path) && File.Exists(path)) return path;
        }
        string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(winget)) return null;
        return Directory.EnumerateDirectories(winget, "Gyan.FFmpeg_*")
            .SelectMany(dir => Directory.EnumerateDirectories(dir, "ffmpeg-*"))
            .Select(dir => Path.Combine(dir, "bin", "ffprobe.exe")).FirstOrDefault(File.Exists);
    }

    public static async Task<TimeSpan?> Duration(string executable, string path)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { "-v", "quiet", "-protocol_whitelist", "file,pipe", "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=1", "-i", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process is null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try {
            string result = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return double.TryParse(result.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null;
        }
        catch (OperationCanceledException) {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            return null;
        }
    }
}

sealed partial class StorageWindow
{
    readonly Dictionary<StorageEntry, TimeSpan> _durations = new();
    Action? _previewCleanup;
    static string VideoTime(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    async Task FillDurations(BrowserPane pane)
    {
        var files = pane.Items.Items.OfType<BrowserRow>().Select(row => row.Entry)
            .Where(entry => entry.Kind == StorageKind.Video && !entry.OnlineOnly && !_durations.ContainsKey(entry)).Take(60).ToArray();
        if (files.Length == 0) { Status("Нет новых локальных видео в списке."); return; }
        try {
            string? probe = await Task.Run(VideoMetadata.FindProbe);
            if (probe is null) { Status("Для длительности видео нужен ffprobe."); return; }
            int count = 0;
            foreach (var file in files) {
                if (_closed || !_panes.Contains(pane)) return;
                if (await VideoMetadata.Duration(probe, file.Path) is { } duration) { _durations[file] = duration; count++; }
            }
            if (_closed) return;
            RenderPane(pane);
            Status($"Длительность добавлена · {count} из {files.Length}");
        }
        catch (Exception ex) { if (!_closed) Status("Не удалось прочитать длительность: " + ex.Message); }
    }

    void ShowVideo(StorageEntry entry)
    {
        var player = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Stretch = Stretch.Uniform, Volume = .6 };
        var layout = new DockPanel();
        var controls = new DockPanel();
        bool playing = true;
        var play = Command("Пауза", "Воспроизвести или приостановить", () => {
            if (playing) player.Pause(); else player.Play();
            playing = !playing;
        });
        AddDock(controls, play, Dock.Left);
        var time = Text("Открываю видео…", 11, PanelControls.Secondary);
        AddDock(controls, time, Dock.Right);
        var slider = new Slider { Minimum = 0, IsMoveToPointEnabled = true, IsEnabled = false, Margin = new Thickness(10, 0, 10, 0) };
        controls.Children.Add(slider);
        AddDock(layout, controls, Dock.Bottom);
        layout.Children.Add(player);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        bool updating = false;
        timer.Tick += (_, _) => {
            play.Content = playing ? "Пауза" : "Играть";
            if (!player.NaturalDuration.HasTimeSpan) return;
            updating = true;
            if (!slider.IsMouseCaptureWithin) slider.Value = player.Position.TotalSeconds;
            updating = false;
            time.Text = $"{VideoTime(player.Position)} / {VideoTime(player.NaturalDuration.TimeSpan)}";
        };
        slider.ValueChanged += (_, _) => { if (!updating && slider.IsEnabled) player.Position = TimeSpan.FromSeconds(slider.Value); };
        player.MediaOpened += (_, _) => {
            if (player.NaturalDuration.HasTimeSpan) { slider.Maximum = player.NaturalDuration.TimeSpan.TotalSeconds; slider.IsEnabled = true; }
            timer.Start();
        };
        player.MediaEnded += (_, _) => { playing = false; player.Pause(); player.Position = TimeSpan.Zero; };
        player.MediaFailed += (_, _) => { timer.Stop(); time.Text = "Формат не открылся в плеере Windows"; play.IsEnabled = false; slider.IsEnabled = false; };
        ShowOverlay(entry.Name, entry.Path, layout);
        _previewCleanup = () => { timer.Stop(); player.Close(); };
        player.Source = new Uri(entry.Path);
        player.Play();
    }
}
