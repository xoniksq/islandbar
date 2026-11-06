using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace IslandBar;

static class StorageRecycle
{
    const uint NoConnectedElements = 0x2000, WarnBeforePermanentDelete = 0x4000;
    const uint RecycleOnDelete = 0x00080000, AddUndoRecord = 0x20000000;
    static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);

    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    class FileOperation { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOperation
    {
        void Advise(nint sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage(nint message);
        void SetProgressDialog(nint dialog);
        void SetProperties(nint properties);
        void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(nint item);
        void ApplyPropertiesToItems(nint items);
        void RenameItem(nint item, nint name, nint sink);
        void RenameItems(nint items, nint name);
        void MoveItem(nint item, nint destination, nint name, nint sink);
        void MoveItems(nint items, nint destination);
        void CopyItem(nint item, nint destination, nint name, nint sink);
        void CopyItems(nint items, nint destination);
        void DeleteItem(nint item, nint sink);
        void DeleteItems(nint items);
        void NewItem(nint destination, uint attributes, nint name, nint template, nint sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    public static bool Send(string[] paths, nint owner)
    {
        var operation = (IFileOperation)new FileOperation();
        try {
            operation.SetOwnerWindow(owner);
            operation.SetOperationFlags(RecycleOnDelete | AddUndoRecord | NoConnectedElements | WarnBeforePermanentDelete);
            foreach (var path in paths) {
                if (!Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is null)
                    throw new IOException("Этот путь нельзя отправить в корзину.");
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ShellItemId, out var item));
                try { operation.DeleteItem(item, 0); }
                finally { Marshal.Release(item); }
            }
            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            return !aborted;
        }
        finally { Marshal.ReleaseComObject(operation); }
    }
}

sealed partial class StorageWindow
{
    void RecycleSelection(StorageEntry[] selected)
    {
        if (_busy || selected.Length == 0 || !CheckBatchSize(selected.Length)) return;
        if (_queue.Active is not null) { Status("Отправка в корзину доступна после завершения очереди."); return; }
        var entries = StorageSelection.Normalize(selected).ToArray();
        SetBusy(true);
        Status("Перемещаю в корзину…");
        string message;
        try {
            bool completed = StorageRecycle.Send(entries.Select(entry => entry.Path).ToArray(), new WindowInteropHelper(this).Handle);
            message = completed ? $"Удалено · {entries.Length}" : "Операция прервана. Список обновлён.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException) {
            message = "Не удалось завершить удаление. " + ex.Message;
        }
        finally { SetBusy(false); }

        foreach (var pane in _panes) {
            var removed = entries.FirstOrDefault(entry => entry.Folder && StorageTransfers.AtOrBelow(pane.Folder, entry.Path) &&
                !Directory.Exists(entry.Path));
            if (removed is null) continue;
            for (string? parent = Path.GetDirectoryName(removed.Path); parent is not null; parent = Path.GetDirectoryName(parent)) {
                if (!Directory.Exists(parent)) continue;
                pane.History.Visit(parent);
                pane.Query = "";
                if (pane == _active) _search.Clear();
                ResetListing(pane);
                _navigation.Visit(parent);
                ScheduleSave();
                break;
            }
        }
        _inbox = null;
        RefreshRecents();
        ArrangePanes(refresh: true);
        _active.Items.Focus();
        Status(message);
    }
}
