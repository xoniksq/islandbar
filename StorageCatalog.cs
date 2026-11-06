using System.Globalization;
using System.IO.Enumeration;

namespace IslandBar;

sealed record StoragePlace(string Name, string Path, string Glyph);
sealed record StorageSpace(string Name, string Glyph, StoragePlace[] Places);
enum StorageKind { Folder, Image, Video, Audio, Archive, Text, Other }
sealed record StorageEntry(string Name, string Path, bool Folder, long Bytes, DateTime Modified, FileAttributes Attributes,
    DateTime Created = default)
{
    static readonly DateTime MissingFileTime = DateTime.FromFileTimeUtc(0);
    public DateTime Added => Created > MissingFileTime ? Created : Modified > MissingFileTime ? Modified : DateTime.MinValue;
    public bool OnlineOnly => (Attributes & (FileAttributes.Offline | (FileAttributes)0x00440000)) != 0;
    public StorageKind Kind => Folder ? StorageKind.Folder : System.IO.Path.GetExtension(Name).ToLowerInvariant() switch {
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" => StorageKind.Image,
        ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" or ".m4v" => StorageKind.Video,
        ".mp3" or ".wav" or ".aac" or ".flac" or ".ogg" or ".m4a" => StorageKind.Audio,
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => StorageKind.Archive,
        ".txt" or ".md" or ".log" or ".csv" or ".json" or ".xml" or ".ini" or ".yml" or ".yaml" or ".cs" or ".js" or ".py" or ".ps1" or ".srt" or ".vtt" => StorageKind.Text,
        _ => StorageKind.Other
    };
}
sealed record StorageVolume(string Name, string Root, long? Free, long? Total);
sealed record StorageListing(StorageEntry[] Entries, bool Limited);

static class StorageCatalog
{
    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string Cloud => Environment.GetEnvironmentVariable("OneDrive") ?? "";
    static string UserFolder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
    const string Folder = "\uE8B7";
    const int ListingLimit = 16000;
    const int SearchLimit = 100_000;

    public static StorageSpace[] Spaces()
    {
        StoragePlace Desk(string name) => new(name, Path.Combine(Desktop, name), Folder);
        return [
            new("Монтаж", "\uE714", Existing(
                Desk("Контент"), Desk("story-shorts"), Desk("шортс гпт"), Desk("шортс гравити"), Desk("шортс истории"),
                new("Видео", UserFolder(Environment.SpecialFolder.MyVideos), "\uE714"),
                new("Откаты", @"D:\откаты", Folder), new("Готовые видео", @"D:\Готовые видива", "\uE74E"))),
            new("Работа", "\uE821", Existing(
                Desk("Проекты"), Desk("Новая папка"), Desk("проект шарага"),
                new("Проекты на C:", @"C:\Projects", Folder),
                new("Документы", UserFolder(Environment.SpecialFolder.MyDocuments), "\uE8A5"))),
            new("Игры", "\uE7FC", Existing(new("Игры на D:", @"D:\Games", Folder),
                new("Steam · D:", @"D:\SteamLibrary", Folder), new("Steam · E:", @"E:\SteamLibrary", Folder))),
            new("Архив", "\uE7B8", Existing(new("Готовые видео", @"D:\Готовые видива", "\uE74E"),
                new("Архив на D:", @"D:\Архив", Folder), new("Архив на E:", @"E:\Архив", Folder)))
        ];
    }

    static StoragePlace[] Existing(params StoragePlace[] places) =>
        places.Where(place => Directory.Exists(place.Path)).ToArray();

    public static StorageVolume[] Volumes()
    {
        return DriveInfo.GetDrives().Select(drive => {
            try {
                if (!drive.IsReady) return new StorageVolume("Носитель недоступен", drive.Name, null, null);
                var name = drive.VolumeLabel;
                if (string.IsNullOrWhiteSpace(name)) name = drive.DriveType switch {
                    DriveType.Fixed when drive.Name.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase) => "Система",
                    DriveType.Removable => "Съёмный носитель",
                    DriveType.Network => "Сетевой диск",
                    _ => "Локальный диск"
                };
                return new StorageVolume(name, drive.Name, drive.AvailableFreeSpace, drive.TotalSize);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return new StorageVolume("Носитель недоступен", drive.Name, null, null);
            }
        }).ToArray();
    }

    public static StorageListing List(string path, CancellationToken token = default, bool showHidden = false)
    {
        var entries = new List<StorageEntry>();
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos()) {
            token.ThrowIfCancellationRequested();
            if (!Visible(item.Attributes, showHidden)) continue;
            try { entries.Add(Entry(item)); }
            catch (FileNotFoundException) { continue; }
            if (entries.Count > ListingLimit) break;
        }
        return new(entries.Take(ListingLimit).ToArray(), entries.Count > ListingLimit);
    }

    public static StorageEntry Entry(FileSystemInfo item) => new(item.Name, item.FullName, item is DirectoryInfo,
        item is FileInfo file ? file.Length : 0, item.LastWriteTimeUtc, item.Attributes, item.CreationTimeUtc);

    static readonly HashSet<string> SearchExcluded = new(StringComparer.OrdinalIgnoreCase) {
        "Windows", "WindowsApps", "WpSystem", "Program Files", "Program Files (x86)", "ProgramData",
        "AppData", "node_modules", "SteamLibrary", "XboxGames", "steamapps", ".git", ".venv"
    };

    public static StorageListing Search(IEnumerable<string> roots, string query, CancellationToken token, bool showHidden = false)
    {
        var pending = new Queue<string>(roots.Where(path => !string.IsNullOrEmpty(path)));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<StorageEntry>();
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int scanned = 0;
        while (pending.TryDequeue(out string? path)) {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(Path.TrimEndingDirectorySeparator(path))) continue;
            try {
                foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos()) {
                    token.ThrowIfCancellationRequested();
                    if (++scanned > SearchLimit || found.Count >= 180) return new([..found], true);
                    var attributes = item.Attributes;
                    if (!Visible(attributes, showHidden)) continue;
                    if (Matches(item.Name, terms)) {
                        try { found.Add(Entry(item)); }
                        catch (IOException) { continue; }
                    }
                    if (item is DirectoryInfo directory && !SearchExcluded.Contains(item.Name) &&
                        directory.LinkTarget is null && (attributes & (FileAttributes.Offline | (FileAttributes)0x00040000)) == 0)
                        pending.Enqueue(directory.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new([..found], false);
    }

    static bool Visible(FileAttributes attributes, bool showHidden) =>
        (attributes & FileAttributes.System) == 0 && (showHidden || (attributes & FileAttributes.Hidden) == 0);

    static bool Matches(string name, string[] terms) => terms.All(term =>
        term.Contains('*') || term.Contains('?')
            ? FileSystemName.MatchesSimpleExpression(term, name, ignoreCase: true)
            : name.Contains(term, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<StorageEntry> FilterAndSort(IEnumerable<StorageEntry> entries, string filter, StorageSort sort, bool reverse = false)
    {
        string[] terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ordered = entries.Where(entry => Matches(entry.Name, terms))
            .OrderByDescending(entry => sort != StorageSort.Added && entry.Folder);
        ordered = sort switch {
            StorageSort.Newest => reverse ? ordered.ThenBy(entry => entry.Modified) : ordered.ThenByDescending(entry => entry.Modified),
            StorageSort.Added => reverse ? ordered.ThenBy(entry => entry.Added) : ordered.ThenByDescending(entry => entry.Added),
            StorageSort.Size => reverse ? ordered.ThenBy(entry => entry.Bytes) : ordered.ThenByDescending(entry => entry.Bytes),
            StorageSort.Type => reverse
                ? ordered.ThenByDescending(entry => Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase)
                : ordered.ThenBy(entry => Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase),
            _ => ordered
        };
        var names = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);
        var byName = sort == StorageSort.Name && reverse ? ordered.ThenByDescending(entry => entry.Name, names) : ordered.ThenBy(entry => entry.Name, names);
        return byName.ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
    }

    static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public static string AddedGroup(DateTime added, DateTime today)
    {
        if (added == DateTime.MinValue) return "Дата неизвестна";
        var date = added.ToLocalTime().Date;
        int daysAgo = (today - date).Days;
        if (daysAgo < 0) return "Будущие даты";
        if (daysAgo == 0) return "Сегодня";
        if (daysAgo == 1) return "Вчера";
        int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        if (daysAgo <= daysSinceMonday) return "На этой неделе";
        if (daysAgo <= daysSinceMonday + 7) return "На прошлой неделе";
        if (date.Year == today.Year && date.Month == today.Month) return "Ранее в этом месяце";
        return Russian.TextInfo.ToTitleCase(date.ToString("MMMM yyyy", Russian));
    }

    public static string[] SearchRoots() => QuickFolders.All().Select(place => place.Path)
        .Append(Cloud).Concat(DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => d.Name)).ToArray();

    public static (StorageEntry Entry, string Origin, string SuggestedFolder)[] Inbox()
    {
        var rows = new List<(StorageEntry, string, string)>();
        foreach (var (path, label) in new[] { (Desktop, "Рабочий стол"), (QuickFolders.Downloads, "Загрузки") }
            .DistinctBy(location => location.Item1, StringComparer.OrdinalIgnoreCase)) {
            try {
                rows.AddRange(List(path).Entries.Where(item => !item.Folder && !item.OnlineOnly
                    && Suggest(item.Name) is not null).OrderByDescending(item => item.Modified).Take(70)
                    .Select(item => (item, label, Suggest(item.Name)!)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return [..rows];
    }

    static string? Suggest(string name)
    {
        string? category = Path.GetExtension(name).ToLowerInvariant() switch {
            ".mp4" or ".mov" or ".mkv" or ".webm" => "Видео",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".psd" or ".svg" => "Изображения",
            ".wav" or ".mp3" or ".flac" or ".m4a" => "Аудио",
            ".docx" or ".xlsx" or ".pptx" or ".pdf" or ".txt" => "Документы",
            ".zip" or ".7z" or ".rar" => "Архивы",
            ".exe" or ".msi" => "Установщики и программы",
            _ => null
        };
        return category is null ? null : Path.Combine(
            Directory.Exists(@"D:\") ? @"D:\Личное" : UserFolder(Environment.SpecialFolder.UserProfile), "Разобранное", category);
    }

    public static string Size(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        int unit = 0;
        double value = bytes;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
