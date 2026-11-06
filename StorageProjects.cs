using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace IslandBar;

sealed record ProjectWindow(StoredApp App, double X, double Y, double Width, double Height);
sealed record StorageProject(string Name, string[] Folders, ProjectWindow[] Windows);

static class StorageProjects
{
    static string DataPath => Path.Combine(Program.Folder, "storage-projects.json");
    public static StorageProject[] Load()
    {
        try { return File.Exists(DataPath) ? JsonSerializer.Deserialize<StorageProject[]>(File.ReadAllText(DataPath)) ?? [] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Program.Log("storage projects: " + ex.Message);
            return [];
        }
    }
    public static void Save(StorageProject[] projects)
    {
        string temp = DataPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(projects));
        File.Move(temp, DataPath, overwrite: true);
    }
}

sealed partial class StorageWindow
{
    public Func<ProjectWindow[]>? CaptureProjectWindows { get; init; }
    public Func<ProjectWindow[], Task<int>>? RestoreProjectWindows { get; init; }

    void ShowProjects()
    {
        var rows = new StackPanel();
        Section(rows, "СОХРАНЁННЫЕ РАСКЛАДКИ");
        var hint = Text("Открой нужные папки во вкладке «Файлы», по одной в каждой панели. Здесь дай раскладке имя и сохрани её, чтобы открыть те же панели позже.",
            13, PanelControls.Secondary, wrap: true);
        hint.Margin = new Thickness(0, 0, 0, 8);
        rows.Children.Add(hint);
        var current = Text("Сейчас открыто · " + string.Join("  /  ", _panes.Select(pane => PathCaption(pane.Folder))),
            12, PanelControls.Silver, wrap: true);
        current.Margin = new Thickness(0, 0, 0, 8);
        rows.Children.Add(current);
        var name = Field("Название проекта");
        rows.Children.Add(PanelControls.Well(Placeholder(name, "Например, Монтаж ролика"), new Thickness(10, 7, 10, 7)));
        var withWindows = new CheckBox { Content = Text("Запомнить также приложения и позиции их окон", 12, PanelControls.Silver, wrap: true),
            Foreground = PanelControls.Silver, Margin = new Thickness(4, 9, 0, 4) };
        if (CaptureProjectWindows is not null) rows.Children.Add(withWindows);
        var save = Command("Сохранить раскладку", "Сохранить текущие панели как проект", () => Try(() => {
            string title = name.Text.Trim();
            if (title.Length == 0) { name.Focus(); Status("Введи название раскладки."); return; }
            var projects = StorageProjects.Load();
            if (projects.Any(p => p.Name.Equals(title, StringComparison.CurrentCultureIgnoreCase)))
                throw new IOException("Проект с таким названием уже есть.");
            var project = new StorageProject(title, _panes.Select(pane => pane.Folder).ToArray(),
                withWindows.IsChecked == true ? CaptureProjectWindows?.Invoke() ?? [] : []);
            StorageProjects.Save([..projects, project]);
            ShowProjects();
            Status("Раскладка сохранена.");
        }));
        PanelControls.FinishTile(save);
        rows.Children.Add(save);
        var saved = StorageProjects.Load();
        if (saved.Length == 0) {
            var empty = Text("Здесь появятся сохранённые проекты.", 12, PanelControls.Secondary, wrap: true);
            empty.Margin = new Thickness(0, 12, 0, 0);
            rows.Children.Add(empty);
        }
        foreach (var project in saved) {
            var card = new StackPanel();
            card.Children.Add(Text(project.Name, 14, weight: FontWeights.SemiBold));
            card.Children.Add(Text(string.Join(" · ", project.Folders.Select(PathCaption)) +
                (project.Windows.Length == 0 ? "" : $" · приложений {project.Windows.Length}"), 12, PanelControls.Secondary, wrap: true));
            var actions = new WrapPanel();
            actions.Children.Add(Command("Вернуть панели", "Восстановить панели проекта", () => OpenProject(project)));
            if (project.Windows.Length > 0 && RestoreProjectWindows is not null)
                actions.Children.Add(Command("Вернуть панели и окна", "Восстановить панели и окна приложений", async () => {
                    if (!OpenProject(project)) return;
                    int failed = await RestoreProjectWindows(project.Windows);
                    if (failed > 0 && !_closed) Status($"Не удалось восстановить окон · {failed}");
                }));
            card.Children.Add(actions);
            var frame = PanelControls.Well(card, new Thickness(10), 9);
            frame.Margin = new Thickness(0, 6, 0, 0);
            rows.Children.Add(frame);
        }
        _page.Content = PanelControls.Scroller(rows, double.PositiveInfinity);
    }

    public bool OpenProject(StorageProject project) => OpenFolderLayout(project.Folders, "Проект открыт · " + project.Name);

    bool OpenFolderLayout(string[] paths, string message)
    {
        if (_busy) return false;
        var folders = paths.Where(Directory.Exists).Take(StorageNavigation.MaxPanes).ToArray();
        if (folders.Length == 0) { Status("Папки раскладки сейчас недоступны."); return false; }
        CancelReads();
        _paneGrid.Children.Clear();
        _panes.Clear();
        _navigation.Panes.Clear();
        foreach (string folder in folders) AttachPane(_navigation.Add(folder)!);
        _navigation.ActiveIndex = 0;
        _active = _panes[0];
        _search.Clear();
        _searchScope.Content = "В папке";
        BuildPaneTabs();
        ShowView("Файлы");
        ScheduleSave();
        Status(message + (folders.Length < paths.Length ? $" · открыто {folders.Length} из {paths.Length}" : ""));
        return true;
    }
}

sealed partial class IslandWindow
{
    ProjectWindow[] CaptureWorkspaceWindows()
    {
        var area = WindowLayouts.WorkArea(_handle);
        var result = new List<ProjectWindow>();
        foreach (var tile in _tiles.Values) {
            if (StoredApp.From(tile.App) is not { } app) continue;
            foreach (nint window in tile.App.Windows.Where(w => Native.IsWindowVisible(w) && !Native.IsIconic(w))) {
                var bounds = WindowLayouts.VisibleBounds(window);
                if (!area.Contains((bounds.Left + bounds.Right) / 2d, (bounds.Top + bounds.Bottom) / 2d)) continue;
                result.Add(new(app, (bounds.Left - area.Left) / area.Width, (bounds.Top - area.Top) / area.Height,
                    (bounds.Right - bounds.Left) / area.Width, (bounds.Bottom - bounds.Top) / area.Height));
                break;
            }
        }
        return [..result];
    }

    async Task<int> RestoreWorkspaceWindows(ProjectWindow[] saved)
    {
        int failed = 0;
        var area = WindowLayouts.WorkArea(_handle);
        var catalog = await _catalog;
        foreach (var item in saved) {
            try {
                nint Find() => catalog.GetApps().FirstOrDefault(app => app.Key.Equals(item.App.Key, StringComparison.OrdinalIgnoreCase))?
                    .Windows.FirstOrDefault(Native.IsWindow) ?? 0;
                nint window = await Task.Run(Find);
                if (window == 0) {
                    Catalog.Launch(item.App.ToApp());
                    for (int attempt = 0; attempt < 18 && window == 0; attempt++) {
                        await Task.Delay(320);
                        window = await Task.Run(Find);
                    }
                }
                if (window == 0) { failed++; continue; }
                if (Native.IsZoomed(window) || Native.IsIconic(window)) Native.ShowWindow(window, 9);
                double x = Math.Clamp(item.X, 0, .9), y = Math.Clamp(item.Y, 0, .9);
                double width = Math.Clamp(item.Width, .1, 1 - x), height = Math.Clamp(item.Height, .1, 1 - y);
                if (!Native.SetWindowPos(window, 0, (int)(area.X + area.Width * x), (int)(area.Y + area.Height * y),
                    (int)(area.Width * width), (int)(area.Height * height), 0x14)) failed++;
            }
            catch (Exception ex) { failed++; Program.Log("project window: " + ex.Message); }
        }
        return failed;
    }
}
