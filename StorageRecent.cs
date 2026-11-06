using System.Text.Json;

namespace IslandBar;

static class StorageRecent
{
    static string History => Path.Combine(Program.Folder, "storage-recent-files.json");
    public static string[] Files()
    {
        try {
            if (File.Exists(History))
                return (JsonSerializer.Deserialize<string[]>(File.ReadAllText(History)) ?? []).Where(File.Exists).Take(40).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return [];
    }
    public static void Add(string path)
    {
        try {
            string temp = History + ".tmp";
            var entries = Files().Prepend(path).Distinct(StringComparer.OrdinalIgnoreCase).Take(40);
            File.WriteAllText(temp, JsonSerializer.Serialize(entries));
            File.Move(temp, History, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("recent file: " + ex.Message); }
    }
}
