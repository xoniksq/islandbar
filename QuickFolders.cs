using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace IslandBar;

sealed record FolderLink(string Name,string Path,string Glyph);
static class QuickFolders
{
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)]Guid folder,uint flags,nint token,out nint path);
    public static string Downloads=>Known("374DE290-123F-4565-9164-39C4925E467B",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads"));
    public static string Known(string id,string fallback)
    {
        nint value=0;try{return SHGetKnownFolderPath(new Guid(id),0,0,out value)==0?Marshal.PtrToStringUni(value)??fallback:fallback;}finally{if(value!=0)Marshal.FreeCoTaskMem(value);}
    }
    public static FolderLink[] All()
    {
        var order=ReadOrder().Select((path,index)=>(path,index))
            .ToDictionary(entry=>entry.path,entry=>entry.index,StringComparer.OrdinalIgnoreCase);
        var hidden=ReadHidden();
        return Defaults().Concat(Custom()).Where(f=>!hidden.Contains(Path.TrimEndingDirectorySeparator(f.Path))&&Directory.Exists(f.Path))
            .DistinctBy(f=>f.Path,StringComparer.OrdinalIgnoreCase)
            .OrderBy(f=>order.GetValueOrDefault(f.Path,int.MaxValue)).ToArray();
    }
    static FolderLink[] Defaults()
    {
        string desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return [
            new("Загрузки",Downloads,"\uE896"),
            new("Рабочий стол",desktop,"\uE8FC"),
            new("Документы",Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"\uE8A5"),
            new("Изображения",Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"\uEB9F"),
            new("Видео",Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"\uE714"),
            new("Папка проектов",Path.Combine(desktop,"Новая папка"),"\uE943")
        ];
    }
    static string OrderSettings=>Path.Combine(Program.Folder,"quick-folders-order.json");
    static string[] ReadOrder()
    {
        try {
            return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(OrderSettings))??[])
                .Where(path=>!string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch(IOException){return [];}
        catch(UnauthorizedAccessException){return [];}
        catch(JsonException){return [];}
    }
    public static void SaveOrder(IEnumerable<string> paths)
    {
        string temp=OrderSettings+".tmp";
        var order=paths.Concat(ReadOrder()).Distinct(StringComparer.OrdinalIgnoreCase);
        File.WriteAllText(temp,JsonSerializer.Serialize(order));
        File.Move(temp,OrderSettings,true);
    }
    static string Settings=>Path.Combine(Program.Folder,"quick-folders.json");
    static string HiddenSettings=>Path.Combine(Program.Folder,"quick-folders-hidden.json");
    static HashSet<string> ReadHidden()
    {
        try {
            return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(HiddenSettings)) ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
    public static FolderLink[] Hidden()
    {
        var hidden = ReadHidden();
        return Defaults().Where(folder => hidden.Contains(Path.TrimEndingDirectorySeparator(folder.Path))).ToArray();
    }
    public static void SetHidden(string path, bool hide)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        var hidden = ReadHidden();
        if (hide ? hidden.Add(path) : hidden.Remove(path)) SaveHidden(hidden);
    }
    static void SaveHidden(HashSet<string> paths)
    {
        string temp = HiddenSettings + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(paths));
        File.Move(temp, HiddenSettings, overwrite: true);
    }
    public static FolderLink[] Custom()
    {
        try { return ReadCustom(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    static FolderLink[] ReadCustom() => File.Exists(Settings)
        ? JsonSerializer.Deserialize<FolderLink[]>(File.ReadAllText(Settings)) ?? []
        : [];

    public static string[] Choose(Window? owner = null, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog { Title = "Добавить в быстрые папки", Multiselect = true };
        if (Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        bool? accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return accepted == true ? dialog.FolderNames : [];
    }

    public static string[] AddMany(IEnumerable<string> paths)
    {
        var existing = ReadCustom();
        var candidates = paths.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var known = Defaults().Concat(existing).Select(folder => Path.TrimEndingDirectorySeparator(folder.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = ReadHidden();
        var restored = candidates.Where(hidden.Remove).ToArray();
        if (restored.Length != 0) SaveHidden(hidden);
        var added = candidates.Where(known.Add)
            .Select(path => new FolderLink(new DirectoryInfo(path).Name, path, "\uE8B7")).ToArray();
        if (added.Length != 0) SaveCustom(existing.Concat(added));
        return [..restored, ..added.Select(folder => folder.Path)];
    }

    public static void Remove(string path) => SaveCustom(ReadCustom()
        .Where(folder => !folder.Path.Equals(path, StringComparison.OrdinalIgnoreCase)));

    static void SaveCustom(IEnumerable<FolderLink> entries)
    {
        string temp = Settings + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, Settings, overwrite: true);
    }
}
