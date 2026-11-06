using System.Windows;
using System.Windows.Controls;

namespace IslandBar;

sealed record RenameStep(string Source, string Target, string Identity);

static class StorageBatchRename
{
    public static string[] Targets(StorageEntry[] entries, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return [];
        int digits = Math.Max(2, entries.Length.ToString().Length);
        return entries.Select((entry, index) => Path.Combine(Path.GetDirectoryName(entry.Path)!,
            prefix + (index + 1).ToString("D" + digits) + (entry.Folder ? "" : Path.GetExtension(entry.Name)))).ToArray();
    }

    public static void Run(RenameStep[] steps)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in steps) {
            if (!names.Add(item.Target) || File.Exists(item.Target) || Directory.Exists(item.Target))
                throw new IOException("Имя уже занято: " + item.Target);
            if (StorageTransfers.GetItemIdentity(item.Source) != item.Identity)
                throw new IOException("Элемент изменился после проверки: " + item.Source);
        }
        var done = new Stack<RenameStep>();
        try {
            foreach (var item in steps) {
                if (StorageTransfers.GetItemIdentity(item.Source) != item.Identity) throw new IOException("Исходный элемент изменился.");
                StorageTransfers.RenamePath(item.Source, item.Target);
                done.Push(item);
            }
        }
        catch (Exception ex) {
            var stranded = new List<string>();
            while (done.TryPop(out var item)) {
                try {
                    if (StorageTransfers.GetItemIdentity(item.Target) != item.Identity) throw new IOException();
                    StorageTransfers.RenamePath(item.Target, item.Source);
                }
                catch { stranded.Add(item.Target); }
            }
            if (stranded.Count != 0) throw new IOException("Остались под новыми именами: " + string.Join(", ", stranded), ex);
            throw;
        }
    }
}

sealed partial class StorageWindow
{
    void ShowBatchRename(BrowserPane pane, StorageEntry[] entries)
    {
        if (_busy || entries.Length < 2 || !CheckBatchSize(entries.Length)) return;
        entries = StorageSelection.Normalize(entries).ToArray();
        Try(() => {
            string[] identities = entries.Select(entry => StorageTransfers.GetItemIdentity(entry.Path)).ToArray();
            var page = new DockPanel();
            var input = Field("Префикс новых имён");
            input.Text = "Сцена_";
            AddDock(page, PanelControls.Well(input, new Thickness(10)), Dock.Top);
            var footer = new WrapPanel();
            footer.Children.Add(Command("Назад", "Вернуться к файлам", () => ShowView("Файлы")));
            var list = new StackPanel();
            var apply = Command("Переименовать", "Применить новые имена", () => Try(() => {
                if (_queue.Active is not null) { Status("Переименование доступно после завершения очереди."); return; }
                var targets = StorageBatchRename.Targets(entries, input.Text);
                if (targets.Length != entries.Length) throw new IOException("Проверь префикс.");
                var steps = entries.Select((entry, index) => new RenameStep(entry.Path, targets[index], identities[index]))
                    .Where(step => !step.Source.Equals(step.Target, StringComparison.OrdinalIgnoreCase)).ToArray();
                StorageBatchRename.Run(steps);
                foreach (var step in steps)
                    if (Directory.Exists(step.Target)) _navigation.Relocate(step.Source, step.Target);
                ShowView("Файлы");
                Status($"Переименовано · {steps.Length}");
                ScheduleSave();
            }));
            PanelControls.FinishTile(apply);
            footer.Children.Add(apply);
            AddDock(page, footer, Dock.Bottom);
            page.Children.Add(PanelControls.Scroller(list, double.PositiveInfinity));
            void Render()
            {
                list.Children.Clear();
                var targets = StorageBatchRename.Targets(entries, input.Text);
                apply.IsEnabled = targets.Length == entries.Length;
                for (int i = 0; i < targets.Length; i++)
                    list.Children.Add(Text(entries[i].Name + "  →  " + Path.GetFileName(targets[i]), 12, wrap: true));
            }
            input.TextChanged += (_, _) => Render();
            Render();
            ShowDetail(page, () => ShowView("Файлы"));
            input.Focus();
            input.SelectAll();
        });
    }
}
