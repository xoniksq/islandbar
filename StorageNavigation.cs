using System.Text.Json;

namespace IslandBar;

enum StorageSort { Name, Newest, Size, Type, Added }
readonly record struct StorageOrder(StorageSort Sort, bool Reverse = false);
[Flags]
enum StoragePlaceSections { Quick = 1, Drives = 2, Workspaces = 4, Recent = 8 }

sealed class StoragePaneState(string initial)
{
    public FolderHistory History { get; } = new(initial);
    public StorageOrder Order { get; set; } = new(StorageSort.Name);
    public StorageOrder DownloadsOrder { get; set; } = new(StorageSort.Added);
    public bool ShowHidden { get; set; }
}

sealed class StorageNavigation
{
    public const int MaxPanes = 6;
    const int RecentLimit = 12;
    readonly Stack<(int Index, StoragePaneState Pane)> _closed = new();
    readonly List<string> _recent = [];
    readonly Dictionary<string, string[]> _placeOrders = new(StringComparer.Ordinal);

    public List<StoragePaneState> Panes { get; } = [new(StorageCatalog.Desktop)];
    public IReadOnlyList<string> Recent => _recent;
    public int ActiveIndex { get; set; }
    public bool FocusMode { get; set; }
    public bool ShowPlaces { get; set; } = true;
    public double PlacesWidth { get; set; } = 190;
    public StoragePlaceSections CollapsedPlaces { get; set; }
    public bool KeepOpen { get; set; }
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 740;

    sealed record PaneData(string Path, StorageSort Sort, bool ShowHidden, bool ReverseSort = false, StorageOrder? DownloadsOrder = null);
    sealed record LayoutData(PaneData[] Panes, int Active, bool FocusMode, bool ShowPlaces, bool KeepOpen,
        double Width, double Height, string[] Recent, double PlacesWidth = 190, StoragePlaceSections CollapsedPlaces = 0,
        Dictionary<string, string[]>? PlaceOrders = null);

    public IEnumerable<T> OrderPlaces<T>(string group, IEnumerable<T> items, Func<T, string> key)
    {
        if (!_placeOrders.TryGetValue(group, out var order)) return items;
        var positions = order.Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);
        int unknown = group == nameof(StoragePlaceSections.Recent) ? -1 : int.MaxValue;
        return items.OrderBy(item => positions.GetValueOrDefault(key(item), unknown));
    }

    public void SavePlacesOrder(string group, string[] ids)
    {
        _placeOrders[group] = group == nameof(StoragePlaceSections.Recent)
            ? ids : ids.Concat(_placeOrders.GetValueOrDefault(group) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Visit(string path)
    {
        _recent.RemoveAll(saved => saved.Equals(path, StringComparison.OrdinalIgnoreCase));
        _recent.Insert(0, path);
        if (_recent.Count > RecentLimit) _recent.RemoveAt(RecentLimit);
        if (_placeOrders.TryGetValue(nameof(StoragePlaceSections.Recent), out var order))
            _placeOrders[nameof(StoragePlaceSections.Recent)] = order.Intersect(_recent, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Relocate(string from, string to)
    {
        foreach (var pane in Panes) pane.History.Relocate(from, to);
        foreach (var entry in _closed) entry.Pane.History.Relocate(from, to);
        var updated = _recent.Select(path => RelocatedPath(path, from, to)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _recent.Clear();
        _recent.AddRange(updated);
        if (_placeOrders.TryGetValue(nameof(StoragePlaceSections.Recent), out var order))
            _placeOrders[nameof(StoragePlaceSections.Recent)] = order.Select(path => RelocatedPath(path, from, to))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string RelocatedPath(string path, string from, string to) =>
        StorageTransfers.AtOrBelow(path, from) ? Path.GetFullPath(Path.Combine(to, Path.GetRelativePath(from, path))) : path;

    public StoragePaneState? Add(string path)
    {
        if (Panes.Count == MaxPanes) return null;
        var pane = new StoragePaneState(path);
        Panes.Add(pane);
        return pane;
    }

    public bool Close(StoragePaneState pane)
    {
        int index = Panes.IndexOf(pane);
        if (index < 0 || Panes.Count == 1) return false;
        _closed.Push((index, pane));
        Panes.RemoveAt(index);
        ActiveIndex = Math.Clamp(ActiveIndex - (index < ActiveIndex ? 1 : 0), 0, Panes.Count - 1);
        return true;
    }

    public StoragePaneState? Reopen()
    {
        if (Panes.Count == MaxPanes || !_closed.TryPop(out var entry)) return null;
        Panes.Insert(Math.Min(entry.Index, Panes.Count), entry.Pane);
        return entry.Pane;
    }

    public static StorageNavigation Load(string path)
    {
        var state = new StorageNavigation();
        try {
            if (!File.Exists(path) || JsonSerializer.Deserialize<LayoutData>(File.ReadAllText(path)) is not { } saved)
                return state;
            var panes = (saved.Panes ?? []).Where(pane => pane is not null && ValidPath(pane.Path)).Take(MaxPanes)
                .Select(pane => new StoragePaneState(pane.Path) {
                    Order = Enum.IsDefined(pane.Sort) ? new(pane.Sort, pane.ReverseSort) : new(StorageSort.Name),
                    DownloadsOrder = pane.DownloadsOrder is { } order && Enum.IsDefined(order.Sort) ? order : new(StorageSort.Added),
                    ShowHidden = pane.ShowHidden
                }).ToArray();
            if (panes.Length != 0) {
                state.Panes.Clear();
                state.Panes.AddRange(panes);
            }
            state.ActiveIndex = Math.Clamp(saved.Active, 0, state.Panes.Count - 1);
            state.FocusMode = saved.FocusMode;
            state.ShowPlaces = saved.ShowPlaces;
            state.PlacesWidth = ClampSize(saved.PlacesWidth, state.PlacesWidth, 132, 320);
            state.CollapsedPlaces = saved.CollapsedPlaces;
            foreach (var (group, order) in saved.PlaceOrders ?? [])
                if (order is not null)
                    state._placeOrders[group] = order.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            state.KeepOpen = saved.KeepOpen;
            state.Width = ClampSize(saved.Width, state.Width, 420, 3840);
            state.Height = ClampSize(saved.Height, state.Height, 360, 2160);
            state._recent.AddRange((saved.Recent ?? []).Where(ValidPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(RecentLimit));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Program.Log("storage layout read: " + ex.Message);
        }
        return state;
    }

    public void Save(string path)
    {
        try {
            var snapshot = new LayoutData(Panes.Select(pane => new PaneData(pane.History.Current, pane.Order.Sort, pane.ShowHidden,
                pane.Order.Reverse, pane.DownloadsOrder)).ToArray(),
                ActiveIndex, FocusMode, ShowPlaces, KeepOpen, Width, Height, [.._recent], PlacesWidth, CollapsedPlaces, _placeOrders);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Program.Log("storage layout save: " + ex.Message);
        }
    }

    static bool ValidPath(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        path.IndexOfAny(Path.GetInvalidPathChars()) == -1;

    static double ClampSize(double value, double fallback, double min, double max) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(value, min, max) : fallback;
}

sealed class FolderHistory(string initial)
{
    readonly List<string> _folders = [initial];
    int _index;

    public string Current => _folders[_index];
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index + 1 < _folders.Count;

    public void Visit(string folder)
    {
        if (Current.Equals(folder, StringComparison.OrdinalIgnoreCase)) return;
        _folders.RemoveRange(_index + 1, _folders.Count - _index - 1);
        _folders.Add(folder);
        if (_folders.Count > 64) _folders.RemoveAt(0);
        _index = _folders.Count - 1;
    }

    public void Relocate(string from, string to)
    {
        for (int i = 0; i < _folders.Count; i++)
            _folders[i] = StorageNavigation.RelocatedPath(_folders[i], from, to);
    }

    public bool TryStep(int direction)
    {
        for (int i = _index + direction; i >= 0 && i < _folders.Count; i += direction) {
            if (!Directory.Exists(_folders[i])) continue;
            _index = i;
            return true;
        }
        return false;
    }
}

readonly record struct StorageLayout(int Start, int Count, int Columns)
{
    public static StorageLayout Fit(double width, double height, int total, int active, bool focus)
    {
        const double panelWidth = 276, panelHeight = 210, gap = 8;
        int columns = focus || width < 2 * panelWidth + gap
            ? 1 : Math.Clamp((int)((width + gap) / (panelWidth + gap)), 2, 3);
        int rows = columns == 1 ? 1 : Math.Clamp((int)((height + gap) / (panelHeight + gap)), 1, 2);
        int count = Math.Min(total, columns * rows);
        int start = Math.Min((active / count) * count, total - count);
        int usedColumns = count == 4 && rows == 2 ? 2 : Math.Min(columns, count);
        return new(start, count, usedColumns);
    }
}
