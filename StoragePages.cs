using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IslandBar;

sealed partial class StorageWindow
{
    void ShowSpace(StorageSpace space)
    {
        ++_pageRevision;
        var rows = new StackPanel();
        rows.Children.Add(Command("←  Файлы", "Вернуться к файлам", () => ShowView("Файлы")));
        Section(rows, "ПОДБОРКА ПАПОК");
        rows.Children.Add(Text(space.Name, 19, weight: FontWeights.SemiBold));
        var hint = Text("Нажми на папку, чтобы открыть её в текущей панели. Подборку можно разложить по отдельным панелям.", 12,
            PanelControls.Secondary, wrap: true);
        hint.Margin = new Thickness(0, 4, 0, 7);
        if (space.Places.Length > 0) rows.Children.Add(hint);
        if (space.Places.Length > 1) {
            int count = Math.Min(StorageNavigation.MaxPanes, space.Places.Length);
            var open = Command($"Открыть в панелях · {count}", "Открыть папки подборки в отдельных панелях", () =>
                OpenFolderLayout(space.Places.Select(place => place.Path).ToArray(), "Подборка открыта · " + space.Name));
            PanelControls.FinishTile(open);
            rows.Children.Add(open);
            if (count < space.Places.Length)
                rows.Children.Add(Text($"Откроются первые {count} доступных папок. Ниже можно выбрать любую из подборки.", 12,
                    PanelControls.Secondary, wrap: true));
        }
        foreach (var place in space.Places)
            rows.Children.Add(Link(place.Glyph, place.Name, () => Navigate(_active, place.Path), place.Path));
        if (space.Places.Length == 0) rows.Children.Add(Text("Сейчас в этой подборке нет доступных папок. Свою раскладку можно собрать во вкладке «Файлы» и сохранить в «Проекты».",
            13, PanelControls.Secondary, wrap: true));
        ShowDetail(PanelControls.Well(PanelControls.Scroller(rows, double.PositiveInfinity), new Thickness(14)), () => ShowView(_view));
    }

    void ShowMap()
    {
        var rows = new StackPanel();
        Section(rows, "РАБОЧИЙ СТОЛ");
        var desktop = new WrapPanel();
        _desktop.Content = DesktopIcons.Hidden ? "Показать значки" : "Скрыть значки";
        if (_desktop.Parent is Panel previous) previous.Children.Remove(_desktop);
        desktop.Children.Add(_desktop);
        _desktop.Click -= ToggleDesktop; _desktop.Click += ToggleDesktop;
        desktop.Children.Add(Command("Открыть папку", "Открыть содержимое рабочего стола", () => Navigate(_active, StorageCatalog.Desktop)));
        desktop.Children.Add(Command("Корзина", "Открыть корзину", () => Launch("shell:RecycleBinFolder")));
        rows.Children.Add(desktop);
        Section(rows, "ПОДКЛЮЧЁННЫЕ ХРАНИЛИЩА");
        var tiles = new UniformGrid { Columns = Width < 700 ? 2 : 3 };
        tiles.SizeChanged += (_, e) => tiles.Columns = e.NewSize.Width < 470 ? 1 : e.NewSize.Width < 730 ? 2 : 3;
        foreach (var volume in StorageCatalog.Volumes()) {
            var card = new StackPanel();
            card.Children.Add(Text(volume.Root.TrimEnd('\\') + "   " + volume.Name, 14, weight: FontWeights.SemiBold));
            if (volume.Free is long free && volume.Total is > 0 and long total) {
                var bar = new ProgressBar { Minimum = 0, Maximum = total, Value = total - free, Height = 4,
                    Foreground = PanelControls.Silver, Background = PanelControls.Outline, Margin = new Thickness(0, 6, 0, 5) };
                card.Children.Add(bar);
                card.Children.Add(Text($"Свободно {StorageCatalog.Size(free)} из {StorageCatalog.Size(total)}", 11, PanelControls.Secondary));
            }
            else card.Children.Add(Text("Недоступен", 11, PanelControls.Secondary));
            var tile = PanelControls.Button(card, volume.Root, 70);
            PanelControls.FinishTile(tile); tile.Margin = new Thickness(0, 0, 6, 6);
            tile.Click += (_, _) => Navigate(_active, volume.Root);
            tiles.Children.Add(tile);
        }
        if (Directory.Exists(StorageCatalog.Cloud))
            tiles.Children.Add(Link("\uE753", "OneDrive", () => Navigate(_active, StorageCatalog.Cloud), StorageCatalog.Cloud));
        rows.Children.Add(tiles);
        Section(rows, "ПОДБОРКИ ПАПОК");
        foreach (var space in StorageCatalog.Spaces()) {
            var area = new WrapPanel();
            area.Children.Add(Link(space.Glyph, space.Name, () => ShowSpace(space)));
            foreach (var place in space.Places)
                area.Children.Add(Link("\uE8B7", place.Name, () => Navigate(_active, place.Path), place.Path));
            rows.Children.Add(area);
        }
        _page.Content = PanelControls.Well(PanelControls.Scroller(rows, double.PositiveInfinity), new Thickness(12));
    }

    void ToggleDesktop(object sender, RoutedEventArgs e) => Try(() => {
        bool hidden = !DesktopIcons.Hidden;
        if (!DesktopIcons.SetHidden(hidden)) throw new IOException("Windows не подтвердила смену видимости значков.");
        _desktop.Content = hidden ? "Показать значки" : "Скрыть значки";
        Status(hidden ? "Значки скрыты. Файлы доступны во вкладке «Файлы»." : "Значки видны на рабочем столе.");
    });

    async Task ShowInbox(int revision)
    {
        if (_inbox is { } cached) {
            _page.Content = cached;
            Status("Отметь нужные файлы или проверь уже выбранные.");
            return;
        }
        _page.Content = Text("Читаю рабочий стол и загрузки…", 13, PanelControls.Secondary);
        try {
            var inbox = await Task.Run(StorageCatalog.Inbox);
            if (_closed || revision != _pageRevision) return;
            var rows = new StackPanel();
            var selected = new HashSet<StoragePlan>();
            var choices = new List<CheckBox>();
            var layout = new DockPanel();
            var review = Command("Перенести выбранное", "Перенести отмеченные элементы", () => ShowPlan([..selected]));
            review.IsEnabled = false;
            PanelControls.FinishTile(review);
            var actions = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            actions.Children.Add(Command("Выбрать все", "Выбрать все предложения", () => {
                foreach (var box in choices) box.IsChecked = true;
            }));
            actions.Children.Add(Command("Снять выбор", "Снять все отметки", () => {
                foreach (var box in choices) box.IsChecked = false;
            }));
            actions.Children.Add(review);
            AddDock(layout, actions, Dock.Bottom);
            void UpdateSelectionCount() {
                review.IsEnabled = selected.Count != 0;
                review.Content = selected.Count == 0 ? "Перенести выбранное" : $"Перенести выбранное · {selected.Count}";
            }
            foreach (var (entry, origin, target) in inbox) {
                var plan = new StoragePlan(entry, target, true);
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var change = Command("Куда…", "Изменить папку назначения", () => {
                    string? folder = PickFolder(Directory.Exists(plan.Folder) ? plan.Folder : Path.GetPathRoot(plan.Folder)!);
                    if (folder is null) return;
                    bool wasSelected = selected.Remove(plan);
                    plan = plan with { Folder = folder };
                    if (wasSelected) selected.Add(plan);
                    if (row.LastChildFill && row.Children[^1] is CheckBox box) box.Content = TransferLabel(plan, origin);
                });
                AddDock(row, change, Dock.Right);
                var choice = new CheckBox { Content = TransferLabel(plan, origin), Foreground = Brushes.White,
                    Template = PanelControls.CheckBox, Padding = new Thickness(6), VerticalContentAlignment = VerticalAlignment.Center };
                choices.Add(choice);
                choice.Checked += (_, _) => { selected.Add(plan); UpdateSelectionCount(); };
                choice.Unchecked += (_, _) => { selected.Remove(plan); UpdateSelectionCount(); };
                row.Children.Add(choice);
                rows.Children.Add(PanelControls.Well(row, new Thickness(8, 4, 6, 4), 10));
            }
            if (inbox.Length == 0) rows.Children.Add(Text("Нет файлов для разбора", 14, PanelControls.Secondary));
            layout.Children.Add(PanelControls.Scroller(rows, double.PositiveInfinity));
            _inbox = layout;
            _page.Content = _inbox;
            Status($"Рабочий стол и загрузки · {inbox.Length} предложений. Отметь нужные файлы.");
        }
        catch (Exception ex) { if (!_closed && revision == _pageRevision) Status(ex.Message); }
    }

    async Task ShowJournal(int revision)
    {
        _page.Content = Text("Читаю журнал…", 13, PanelControls.Secondary);
        try {
            var history = await Task.Run(_transfers.Read);
            if (_closed || revision != _pageRevision) return;
            var undone = history.Where(item => item.Completed && item.UndoOf is not null)
                .Select(item => item.UndoOf!.Value).ToHashSet();
            var rows = new StackPanel();
            foreach (var item in history.Take(250)) {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
                bool hasBackup = item.HasReplacedBackup;
                bool restored = undone.Contains(item.Id) && !hasBackup;
                if (item.Move && item.Completed && item.UndoOf is null && !restored)
                    AddDock(row, Command("Вернуть", "Вернуть на исходное место", async () => await Undo(item)), Dock.Right);
                if (item.RecoveryPath is { } recovery && Directory.Exists(recovery))
                    AddDock(row, Command("Открыть", "Открыть папку с уже перенесёнными данными", () => Navigate(_active, recovery)), Dock.Right);
                else if (hasBackup && item.ReplacedBackup is { } backup)
                    AddDock(row, Command("Прежняя версия", "Открыть папку с сохранённым файлом", () => Navigate(_active, Path.GetDirectoryName(backup)!)), Dock.Right);
                var labels = new StackPanel();
                string state = restored ? "Отменено" : item.Completed ? "Готово" : item.State;
                string action = item.UndoOf is not null ? "Возврат" : hasBackup ? "Замена" : item.Move ? "Перенос" : "Копирование";
                labels.Children.Add(Text($"{action} · {state} · {item.Time:dd.MM HH:mm}", 12, weight: FontWeights.SemiBold));
                labels.Children.Add(Text(item.Source + "   →   " + item.Destination, 11, PanelControls.Secondary, wrap: true));
                if (item.Error is not null) labels.Children.Add(Text(item.Error, 11, PanelControls.Secondary, wrap: true));
                if (item.RecoveryPath is { } saved && Directory.Exists(saved))
                    labels.Children.Add(Text("Уже перенесённые данные · " + saved, 11, PanelControls.Secondary, wrap: true));
                row.Children.Add(labels); rows.Children.Add(PanelControls.Well(row, new Thickness(10), 10));
            }
            if (history.Length == 0) rows.Children.Add(Text("Журнал пока пуст", 14, PanelControls.Secondary));
            _page.Content = PanelControls.Scroller(rows, double.PositiveInfinity);
        }
        catch (Exception ex) { if (!_closed && revision == _pageRevision) Status(ex.Message); }
    }

    async Task Undo(StorageTransfer transfer)
    {
        if (_busy) return;
        if (_queue.Active is not null) { Status("Вернуть перенос можно после завершения очереди."); return; }
        string message;
        SetBusy(true);
        Status("Проверяю перед возвратом…");
        try {
            var result = await RestoreTransfer(transfer);
            message = result.Completed ? transfer.IsFolder ? "Папка возвращена." : "Файл возвращён." : result.Error ?? result.State;
        }
        catch (Exception ex) { message = ex.Message; }
        finally { SetBusy(false); }
        ShowView("Журнал");
        Status(message);
    }

    async Task<StorageTransfer> RestoreTransfer(StorageTransfer transfer)
    {
        var result = await Task.Run(() => _transfers.Undo(transfer.Id));
        if (result.Completed) {
            _queue.MarkUndone(transfer.Id);
            if (result.IsFolder) {
                _navigation.Relocate(result.Source, result.Destination);
                ScheduleSave();
            }
            _inbox = null;
        }
        return result;
    }

    async Task UndoLatest()
    {
        if (_busy) return;
        if (_queue.Active is not null) { Status("Отмена доступна после завершения очереди."); return; }
        if (_queue.LastUndo is not { } action) { Status(_queue.UndoHint); return; }
        SetBusy(true);
        Status("Проверяю перед отменой…");
        int done = 0;
        string message;
        try {
            if (action.Renames is { } renamed) {
                var reverse = renamed.Reverse().Select(item => new RenameStep(item.Target, item.Source, item.Identity)).ToArray();
                await Task.Run(() => StorageBatchRename.Run(reverse));
                foreach (var item in reverse)
                    if (Directory.Exists(item.Target)) _navigation.Relocate(item.Source, item.Target);
                _queue.FinishUndo(action);
                ScheduleSave();
                message = $"Прежние имена возвращены · {reverse.Length}";
            } else {
                foreach (var transfer in action.Moves.ToArray().Reverse()) {
                    var result = await RestoreTransfer(transfer);
                    if (!result.Completed) throw new IOException(result.Error ?? result.State);
                    done++;
                }
                message = $"Перенос отменён · {done}";
            }
        }
        catch (Exception ex) {
            message = (done == 0 ? "Отмена остановлена. " : $"Возвращено · {done}. ") + ex.Message;
        }
        finally {
            SetBusy(false);
            _inbox = null;
            ArrangePanes(refresh: true);
            _active.Items.Focus();
        }
        Status(message);
    }

}
