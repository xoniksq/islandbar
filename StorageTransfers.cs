using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace IslandBar;

sealed record StoragePlan(StorageEntry Source, string Folder, bool Move, string? ExpectedFolderIdentity = null,
    string? ExpectedDestinationIdentity = null, string? ExpectedFileIdentity = null, string? DestinationName = null,
    StorageReplacement? Replacement = null)
{
    public string Destination => Path.Combine(Folder, DestinationName ?? Source.Name);
    public string? InvalidReason {
        get {
            string name = DestinationName ?? Source.Name;
            if (Path.GetDirectoryName(Source.Path) is null || string.IsNullOrEmpty(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
                return "Корень диска нельзя переносить или копировать как папку.";
            if (Source.Folder && StorageTransfers.AtOrBelow(Folder, Source.Path))
                return "Папку нельзя перенести в неё саму или в её подпапку.";
            if (Source.Path.Equals(Destination, StringComparison.OrdinalIgnoreCase))
                return "Элемент уже здесь. Выбери другую папку назначения.";
            return null;
        }
    }
    public string? BlockReason => InvalidReason ?? (Replacement is null && (File.Exists(Destination) || Directory.Exists(Destination))
        ? "В папке назначения уже есть элемент с этим именем." : null);
}

sealed record StorageReplacement(string Identity, DateTime Modified, long Bytes);
sealed record StorageConflict(StorageEntry Existing, StorageReplacement? Replacement);

sealed record StorageTransfer(Guid Id, string Source, string Destination, bool Move, DateTime Time,
    string State, string? Hash = null, Guid? UndoOf = null, string? Error = null,
    bool IsFolder = false, string? FolderIdentity = null, string? RecoveryPath = null,
    string? ReplacedBackup = null, StorageReplacement? ReplacedVersion = null)
{
    [JsonIgnore]
    public bool HasReplacedBackup => ReplacedBackup is not null && File.Exists(ReplacedBackup);
    [JsonIgnore]
    public bool Completed => State == "Готово" ||
        (State == "Проверено" && (IsFolder
            ? (!Move || !Directory.Exists(Source)) && StorageTransfers.HasFolderIdentity(Destination, FolderIdentity)
            : File.Exists(Destination) && (!Move || !File.Exists(Source))));
}

sealed partial class StorageTransfers(string journal)
{
    readonly object _sync = new();
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public StorageTransfer[] Read()
    {
        lock (_sync) return Load().AsEnumerable().Reverse().ToArray();
    }

    List<StorageTransfer> Load() => File.Exists(journal)
        ? JsonSerializer.Deserialize<List<StorageTransfer>>(File.ReadAllText(journal)) ?? []
        : [];

    void Save(List<StorageTransfer> records, StorageTransfer row)
    {
        int index = records.FindIndex(item => item.Id == row.Id);
        if (index >= 0) records[index] = row;
        else records.Add(row);
        string temp = journal + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) {
            JsonSerializer.Serialize(file, records, JsonOptions);
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, journal, overwrite: true);
    }

    public StorageTransfer Execute(StoragePlan plan)
    {
        lock (_sync) return Execute(plan, Load());
    }

    public StorageTransfer Undo(Guid id)
    {
        lock (_sync) {
            var records = Load();
            var previous = records.Single(row => row.Id == id);
            if (!previous.Move || previous.UndoOf is not null || !previous.Completed ||
                (previous.IsFolder ? previous.FolderIdentity is null : previous.Hash is null))
                throw new IOException("Для этой записи отмена недоступна.");
            if (records.LastOrDefault(row => row.UndoOf == id && row.Completed) is { } reversed) {
                if (!previous.HasReplacedBackup) throw new IOException("Эта операция уже отменена.");
                RestoreReplacedFile(previous);
                return reversed;
            }
            if (previous.ReplacedBackup is not null) VerifyBackup(previous);
            string original = Path.GetDirectoryName(previous.Source)!;
            if (!Directory.Exists(original)) throw new DirectoryNotFoundException("Исходная папка недоступна.");
            FileSystemInfo source = previous.IsFolder ? new DirectoryInfo(previous.Destination) : new FileInfo(previous.Destination);
            var result = Execute(new StoragePlan(StorageCatalog.Entry(source), original, true,
                DestinationName: Path.GetFileName(previous.Source)), records, previous);
            if (result.Completed && previous.ReplacedBackup is not null) RestoreReplacedFile(previous);
            return result;
        }
    }

    StorageTransfer Execute(StoragePlan plan, List<StorageTransfer> records, StorageTransfer? undo = null)
    {
        if (plan.BlockReason is { } reason) throw new IOException(reason);
        if (plan.ExpectedDestinationIdentity is { } target && !HasFolderIdentity(plan.Folder, target))
            throw new IOException("Папка назначения изменилась после проверки.");
        if (plan.Replacement is not null && (plan.Source.Folder || !plan.Move))
            throw new IOException("Эту замену нельзя выполнить.");
        if (plan.Source.Folder) return TransferFolder(plan, records, undo);
        var entry = StorageCatalog.Entry(new FileInfo(plan.Source.Path));
        if (entry.Folder || entry.OnlineOnly || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Перенос доступен для обычных локальных файлов.");
        if (entry.Modified != plan.Source.Modified || entry.Bytes != plan.Source.Bytes)
            throw new IOException("Файл изменился после предпросмотра. Обнови список.");
        if (plan.ExpectedFileIdentity is { } expected && GetItemIdentity(entry.Path) != expected)
            throw new IOException("Исходный файл был заменён после проверки.");
        if (plan.Move && (entry.Attributes & FileAttributes.ReadOnly) != 0)
            throw new IOException("Файл доступен только для чтения.");
        var row = new StorageTransfer(Guid.NewGuid(), entry.Path, plan.Destination, plan.Move,
            DateTime.Now, "Подготовлено", UndoOf: undo?.Id);
        if (plan.Replacement is { } selected) row = row with {
            ReplacedBackup = Path.Combine(plan.Folder, ".island-undo-" + row.Id.ToString("N"), Path.GetFileName(plan.Destination)),
            ReplacedVersion = selected
        };
        Save(records, row);
        string stage = Path.Combine(plan.Folder, ".island-" + row.Id.ToString("N") + ".part");
        bool published = false;
        try {
            Directory.CreateDirectory(plan.Folder);
            File.Copy(entry.Path, stage);
            // Lock the original against writes and replacement through verification and removal.
            using (var source = OpenSource(entry.Path, plan.Move))
            using (var copy = File.Open(stage, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) {
                string? sourceIdentity = plan.ExpectedFileIdentity is not null || plan.Replacement is not null
                    ? ReadIdentity(source.SafeFileHandle).Value : null;
                if (source.Length != entry.Bytes || File.GetLastWriteTimeUtc(source.SafeFileHandle) != entry.Modified ||
                    plan.ExpectedFileIdentity is { } identity && sourceIdentity != identity)
                    throw new IOException("Файл изменился после предпросмотра. Обнови список.");
                if (plan.Replacement is { } replaced && sourceIdentity == replaced.Identity)
                    throw new IOException("Исходный и существующий файлы указывают на один объект.");
                string hash = Convert.ToHexString(SHA256.HashData(source));
                if (undo is not null && hash != undo.Hash)
                    throw new IOException("Файл изменился после переноса. Отмена остановлена.");
                if (hash != Convert.ToHexString(SHA256.HashData(copy)))
                    throw new IOException("Копия отличается от оригинала.");
                row = row with { State = "Проверено", Hash = hash };
                Save(records, row);
                if (plan.ExpectedDestinationIdentity is { } destination && !HasFolderIdentity(plan.Folder, destination))
                    throw new IOException("Папка назначения изменилась после проверки.");
                if (plan.Replacement is { } replacement) BackupReplacement(plan.Destination, row.ReplacedBackup!, replacement);
                File.Move(stage, plan.Destination);
                published = true;
                byte deleteFile = 1;
                if (plan.Move && !SetFileInformationByHandle(source.SafeFileHandle, 4, ref deleteFile, 1))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            row = row with { State = "Готово" };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) {
            if (!published) {
                try { File.Delete(stage); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) {
                    Program.Log("storage staging: " + cleanup.Message);
                }
                row = RollBackReplacement(row);
            }
            row = row with { State = published ? "Копия сохранена" : "Ошибка", Error = ex.Message };
        }
        Save(records, row);
        return row;
    }

    public static StorageConflict? InspectConflict(StoragePlan plan)
    {
        FileSystemInfo item;
        if (Directory.Exists(plan.Destination)) item = new DirectoryInfo(plan.Destination);
        else if (File.Exists(plan.Destination)) item = new FileInfo(plan.Destination);
        else return null;
        var existing = StorageCatalog.Entry(item);
        if (plan.Source.Folder || existing.Folder || existing.OnlineOnly ||
            (existing.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
            return new(existing, null);
        string identity = GetItemIdentity(existing.Path);
        return new(existing, identity == plan.ExpectedFileIdentity ? null : new(identity, existing.Modified, existing.Bytes));
    }

    public static StoragePlan KeepBoth(StoragePlan plan)
    {
        string extension = plan.Source.Folder ? "" : Path.GetExtension(plan.Source.Name);
        string stem = plan.Source.Name[..(plan.Source.Name.Length - extension.Length)];
        for (int i = 2; i < 10000; i++) {
            var candidate = plan with { DestinationName = $"{stem} ({i}){extension}", Replacement = null };
            if (candidate.BlockReason is null) return candidate;
        }
        throw new IOException("Не удалось подобрать свободное имя.");
    }

    static void BackupReplacement(string destination, string backup, StorageReplacement expected)
    {
        var existing = StorageCatalog.Entry(new FileInfo(destination));
        if (existing.OnlineOnly || (existing.Attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
            throw new IOException("Существующий файл нельзя заменить.");
        using var file = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!MatchesReplacement(file, expected))
            throw new IOException("Существующий файл изменился после выбора действия.");
        string folder = Path.GetDirectoryName(backup)!;
        Directory.CreateDirectory(folder);
        File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden);
        File.Move(destination, backup);
        if (GetItemIdentity(backup) != expected.Identity)
            throw new IOException("Существующий файл изменился во время замены.");
    }

    static StorageTransfer RollBackReplacement(StorageTransfer row)
    {
        if (row.ReplacedBackup is not { } backup) return row;
        if (File.Exists(backup)) {
            try { File.Move(backup, row.Destination); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return row with { RecoveryPath = Path.GetDirectoryName(backup) };
            }
        }
        RemoveEmptyBackupFolder(backup);
        return row with { ReplacedBackup = null, ReplacedVersion = null };
    }

    static void VerifyBackup(StorageTransfer row)
    {
        if (row.ReplacedBackup is not { } path || row.ReplacedVersion is not { } version || !File.Exists(path))
            throw new IOException("Сохранённая версия файла недоступна.");
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!MatchesReplacement(file, version))
            throw new IOException("Сохранённая версия файла недоступна или была изменена.");
    }

    static bool MatchesReplacement(FileStream file, StorageReplacement expected) =>
        ReadIdentity(file.SafeFileHandle).Value == expected.Identity && file.Length == expected.Bytes &&
        File.GetLastWriteTimeUtc(file.SafeFileHandle) == expected.Modified;

    static void RestoreReplacedFile(StorageTransfer row)
    {
        VerifyBackup(row);
        File.Move(row.ReplacedBackup!, row.Destination);
        RemoveEmptyBackupFolder(row.ReplacedBackup!);
    }

    static void RemoveEmptyBackupFolder(string backup)
    {
        string folder = Path.GetDirectoryName(backup)!;
        try { if (Directory.Exists(folder)) Directory.Delete(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("storage backup cleanup: " + ex.Message); }
    }

    static FileStream OpenSource(string path, bool move)
    {
        if (!move) return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        const uint readAndDelete = 0x80010000, shareRead = 1, openExisting = 3, sequential = 0x08000000;
        var handle = CreateFile(path, readAndDelete, shareRead, 0, openExisting, sequential, 0);
        if (!handle.IsInvalid) return new FileStream(handle, FileAccess.Read);
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref byte info, uint size);
}
