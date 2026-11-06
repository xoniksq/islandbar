using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32.SafeHandles;

namespace IslandBar;

sealed partial class StorageTransfers
{
    public static bool AtOrBelow(string path, string parent)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        parent = Path.TrimEndingDirectorySeparator(parent);
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasFolderIdentity(string path, string? identity)
    {
        if (identity is null || !Directory.Exists(path)) return false;
        try { return InspectDirectory(path).Identity == identity; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) { return false; }
    }

    public static string GetFolderIdentity(string path) => InspectDirectory(path).Identity;

    public static string GetItemIdentity(string path)
    {
        using var handle = OpenMetadata(path);
        return ReadIdentity(handle).Value;
    }

    StorageTransfer TransferFolder(StoragePlan plan, List<StorageTransfer> records, StorageTransfer? undo)
    {
        var original = new DirectoryInfo(plan.Source.Path);
        if (!original.Exists) throw new DirectoryNotFoundException("Исходная папка недоступна.");
        if (original.LinkTarget is not null) throw new IOException("Эта папка является ссылкой.");
        var source = InspectDirectory(original.FullName);
        var target = InspectDirectory(plan.Folder);
        if (AtOrBelow(target.Path, source.Path))
            throw new IOException("Папку нельзя перенести в неё саму или в её подпапку.");
        if (plan.ExpectedFolderIdentity is not null && source.Identity != plan.ExpectedFolderIdentity)
            throw new IOException("Исходная папка была заменена после предпросмотра.");
        if (undo is not null && source.Identity != undo.FolderIdentity)
            throw new IOException("На месте перенесённой папки теперь находится другая папка.");

        var id = Guid.NewGuid();
        bool acrossVolumes = source.Volume != target.Volume;
        string? stage = acrossVolumes || !plan.Move ? Path.Combine(plan.Folder, ".island-folder-" + id.ToString("N")) : null;
        var row = new StorageTransfer(id, original.FullName, plan.Destination, plan.Move, DateTime.Now, "Подготовлено",
            UndoOf: undo?.Id, IsFolder: true, RecoveryPath: stage);
        Save(records, row);
        try {
            if (stage is not null) {
                CheckTransferTree(original);
                RecheckFolderTransfer(plan, source, target);
                // Only publish the final name once the staging operation finishes.
                if (plan.Move) {
                    FileSystem.MoveDirectory(original.FullName, stage, overwrite: false);
                    if (Directory.Exists(original.FullName))
                        throw new IOException("Часть содержимого осталась в исходной папке.");
                } else FileSystem.CopyDirectory(original.FullName, stage, overwrite: false);
                row = row with { State = "Проверено", FolderIdentity = InspectDirectory(stage).Identity };
                Save(records, row);
                if (!HasFolderIdentity(plan.Folder, target.Identity))
                    throw new IOException("Папка назначения изменилась.");
                RenamePath(stage, plan.Destination);
            } else {
                row = row with { State = "Проверено", FolderIdentity = source.Identity };
                Save(records, row);
                RecheckFolderTransfer(plan, source, target);
                RenamePath(original.FullName, plan.Destination);
            }
            row = row with { State = "Готово", RecoveryPath = null };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException) {
            bool hasRecovery = stage is not null && Directory.Exists(stage);
            row = row with {
                State = hasRecovery ? plan.Move ? "Перенос прерван" : "Копирование прервано" : "Ошибка",
                RecoveryPath = hasRecovery ? stage : null,
                Error = ex.Message
            };
        }
        Save(records, row);
        return row;
    }

    static void RecheckFolderTransfer(StoragePlan plan, DirectorySnapshot source, DirectorySnapshot target)
    {
        if (!HasFolderIdentity(plan.Source.Path, source.Identity) || !HasFolderIdentity(plan.Folder, target.Identity))
            throw new IOException("Исходная папка или место назначения изменились.");
        if (plan.BlockReason is { } reason) throw new IOException(reason);
    }

    static void CheckTransferTree(DirectoryInfo source)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(source);
        while (pending.TryPop(out var directory)) {
            RejectSpecial(directory);
            foreach (var item in directory.EnumerateFileSystemInfos()) {
                if (item is DirectoryInfo child) pending.Push(child);
                else RejectSpecial(item);
            }
        }

        static void RejectSpecial(FileSystemInfo item)
        {
            const FileAttributes cloudRecall = (FileAttributes)0x00440000;
            if ((item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline | cloudRecall)) != 0)
                throw new IOException($"Для этой операции нужны локальные файлы без ссылок: {item.FullName}");
        }
    }

    public static void RenamePath(string from, string to)
    {
        if (!MoveFileEx(Path.GetFullPath(from), Path.GetFullPath(to), 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    sealed record DirectorySnapshot(string Path, ulong Volume, string Identity);

    static DirectorySnapshot InspectDirectory(string path)
    {
        using var handle = OpenMetadata(path);
        var id = ReadIdentity(handle);
        var resolved = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle, resolved, (uint)resolved.Capacity, 0);
        if (length >= resolved.Capacity) {
            resolved.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, resolved, (uint)resolved.Capacity, 0);
        }
        if (length == 0 || length >= resolved.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(resolved.ToString(), id.Volume, id.Value);
    }

    static SafeFileHandle OpenMetadata(string path)
    {
        const uint readAttributes = 0x80, shareAll = 7, openExisting = 3, backupSemantics = 0x02000000;
        var handle = CreateFile(path, readAttributes, shareAll, 0, openExisting, backupSemantics, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    static (ulong Volume, string Value) ReadIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(handle, 18, out var id, 24))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (id.Volume, $"{id.Volume:X16}{id.First:X16}{id.Second:X16}");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DirectoryId { public ulong Volume, First, Second; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out DirectoryId id, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder name, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool MoveFileEx(string source, string destination, uint flags);
}
