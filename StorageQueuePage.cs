using System.Windows;
using System.Windows.Controls;

namespace IslandBar;

sealed partial class StorageWindow
{
    StorageConflict? _shownConflict;
    StorageJob? _shownCompletion;

    public void OpenQueue()
    {
        if (!_initialized) { _view = "Очередь"; return; }
        ShowView("Очередь");
        QueueChanged();
    }

    void QueueChanged()
    {
        if (_closed || !_initialized) return;
        var active = _queue.Active;
        if (_queue.Conflict is { } conflict) {
            Status("Очередь · нужно выбрать действие при совпадении имён.");
            if (!ReferenceEquals(_shownConflict, conflict.Conflict) && IsVisible && IsActive && _overlay is null) {
                _shownConflict = conflict.Conflict;
                ShowQueueConflict(conflict.Plan, conflict.Conflict);
            }
        } else {
            if (_dismissConflict is not null) ClosePreview();
            _shownConflict = null;
            if (active is not null) Status($"Очередь · {active.Finished}/{active.Plans.Length} · {active.Current}");
        }
        if (_view == "Очередь") ShowQueuePage();
        if (_queue.Last is { } last && !ReferenceEquals(last, _shownCompletion)) {
            _shownCompletion = last;
            _inbox = null;
            if (_view == "Файлы" && ReferenceEquals(_page.Content, _files)) ArrangePanes(refresh: true);
            if (active is null) Status(JobSummary(last));
        }
    }

    static string JobSummary(StorageJob job) =>
        $"Готово {job.Done}/{job.Plans.Length} · пропущено {job.Skipped} · ошибок {job.Errors.Count}" +
        (job.Stop ? " · остановлено" : "");

    void ShowQueuePage()
    {
        var rows = new StackPanel();
        Section(rows, "ФОНОВЫЕ ОПЕРАЦИИ");
        if (_queue.Active is { } job) {
            rows.Children.Add(Text($"{job.Current ?? "Подготовка"} · {job.Finished}/{job.Plans.Length}", 15, weight: FontWeights.SemiBold));
            rows.Children.Add(new ProgressBar { Maximum = job.Plans.Length, Value = job.Finished, Height = 5, Margin = new Thickness(0, 8, 0, 8) });
            rows.Children.Add(Text($"Осталось отдельных файлов · {StorageCatalog.Size(job.Remaining)}" +
                (job.HasFolders ? " · папки учитываются по количеству" : ""), 12, PanelControls.Secondary, wrap: true));
            if (_queue.Conflict is { } conflict)
                rows.Children.Add(Command("Разрешить совпадение", "Открыть карточку совпадения", () => {
                    _shownConflict = conflict.Conflict;
                    ShowQueueConflict(conflict.Plan, conflict.Conflict);
                }));
            rows.Children.Add(Command("Остановить после текущего", "Остановить текущую задачу", _queue.StopCurrent));
            if (_queue.Waiting > 0) rows.Children.Add(Text($"Задач в ожидании · {_queue.Waiting}", 12));
        } else rows.Children.Add(Text("Очередь свободна", 13, PanelControls.Secondary));
        if (_queue.Recent.Count > 0) Section(rows, "НЕДАВНИЕ ЗАДАЧИ");
        foreach (var recent in _queue.Recent) {
            var card = new StackPanel();
            card.Children.Add(Text(recent.Plans[0].Source.Name + " → " + PathCaption(recent.Plans[0].Folder),
                13, weight: FontWeights.SemiBold, wrap: true));
            card.Children.Add(Text(JobSummary(recent), 12));
            foreach (var error in recent.Errors)
                card.Children.Add(Text($"{error.Plan.Source.Name} · {error.Error}", 12, PanelControls.Secondary, wrap: true));
            if (!recent.Retried && recent.Errors.Any(error => error.Retry))
                card.Children.Add(Command("Повторить ошибки", "Повторить только неудачные элементы", () => _queue.Retry(recent)));
            var frame = PanelControls.Well(card, new Thickness(8), 8);
            frame.Margin = new Thickness(0, 0, 0, 6);
            rows.Children.Add(frame);
        }
        _page.Content = PanelControls.Well(PanelControls.Scroller(rows, double.PositiveInfinity), new Thickness(14));
    }
}
