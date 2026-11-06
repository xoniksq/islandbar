using System.ComponentModel;

namespace IslandBar;

sealed record CheckedTransfer(StoragePlan Plan, string? Error);

static class StorageSelection
{
    public const int MaxBatch = 500;
    static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    public static StoragePlan[] ToPlans(IEnumerable<StorageEntry> entries, string folder, bool move)
        => Normalize(entries).Select(entry => new StoragePlan(entry, folder, move)).ToArray();

    public static IEnumerable<StorageEntry> Normalize(IEnumerable<StorageEntry> entries)
    {
        var unique = entries.DistinctBy(entry => entry.Path, Paths).ToArray();
        var folders = unique.Where(entry => entry.Folder).Select(entry => entry.Path).ToHashSet(Paths);
        return unique.Where(entry => !CoveredBySelection(entry.Path, folders));
    }

    static bool CoveredBySelection(string path, HashSet<string> folders)
    {
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (folders.Contains(parent)) return true;
        return false;
    }

    public static CheckedTransfer[] Check(StoragePlan[] plans, bool allowConflicts = false)
    {
        var duplicates = plans.GroupBy(plan => plan.Destination, Paths).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(Paths);
        var destinations = new Dictionary<string, string?>(Paths);
        var checkedPlans = new List<CheckedTransfer>(plans.Length);
        foreach (var plan in plans) {
            string? error = allowConflicts ? plan.InvalidReason : duplicates.Contains(plan.Destination)
                ? "В выборке есть несколько элементов с таким именем."
                : plan.BlockReason;
            if (error is not null) {
                checkedPlans.Add(new(plan, error));
                continue;
            }
            try {
                if (!destinations.TryGetValue(plan.Folder, out var target)) {
                    target = Directory.Exists(plan.Folder) ? StorageTransfers.GetFolderIdentity(plan.Folder) : null;
                    destinations.Add(plan.Folder, target);
                }
                if (plan.Source.Folder) {
                    var directory = new DirectoryInfo(plan.Source.Path);
                    if (!directory.Exists) throw new DirectoryNotFoundException("Исходная папка недоступна.");
                    if (directory.LinkTarget is not null) throw new IOException("Папка является ссылкой.");
                    checkedPlans.Add(new(plan with {
                        ExpectedFolderIdentity = StorageTransfers.GetFolderIdentity(directory.FullName),
                        ExpectedDestinationIdentity = target
                    }, null));
                } else {
                    var source = StorageCatalog.Entry(new FileInfo(plan.Source.Path));
                    if (source.OnlineOnly || (source.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Файл нужно сначала скачать или открыть по его реальному пути.");
                    if (plan.Move && (source.Attributes & FileAttributes.ReadOnly) != 0)
                        throw new IOException("Файл доступен только для чтения.");
                    checkedPlans.Add(new(plan with {
                        Source = source,
                        ExpectedFileIdentity = StorageTransfers.GetItemIdentity(source.Path),
                        ExpectedDestinationIdentity = target
                    }, null));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) {
                checkedPlans.Add(new(plan, ex.Message));
            }
        }
        return [..checkedPlans];
    }
}
