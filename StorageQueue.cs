namespace IslandBar;

enum QueueChoice { Replace, Skip, KeepBoth, Stop }

sealed record StorageUndo(List<StorageTransfer> Moves, RenameStep[]? Renames = null);

sealed class StorageJob(StoragePlan[] plans, int skipped, bool prepare)
{
    public StoragePlan[] Plans { get; } = plans;
    public int Skipped { get; set; } = skipped;
    public bool Prepare { get; } = prepare;
    public int Finished { get; set; }
    public int Done { get; set; }
    public long Bytes { get; set; } = plans.Where(p => !p.Source.Folder).Sum(p => p.Source.Bytes);
    public long Remaining { get; set; } = plans.Where(p => !p.Source.Folder).Sum(p => p.Source.Bytes);
    public bool HasFolders { get; } = plans.Any(p => p.Source.Folder);
    public bool Stop { get; set; }
    public bool Retried { get; set; }
    public string? Current { get; set; }
    public List<(StoragePlan Plan, string Error, bool Retry)> Errors { get; } = [];
}

sealed class StorageQueue(StorageTransfers transfers)
{
    readonly Queue<StorageJob> _pending = new();
    readonly List<StorageJob> _recent = [];
    readonly List<StorageUndo> _undo = [];
    TaskCompletionSource<QueueChoice>? _decision;
    bool _running;
    public StorageJob? Active { get; private set; }
    public StorageJob? Last => _recent.FirstOrDefault();
    public IReadOnlyList<StorageJob> Recent => _recent;
    public (StoragePlan Plan, StorageConflict Conflict)? Conflict { get; private set; }
    public int Waiting => _pending.Count;
    public StorageUndo? LastUndo => _undo.LastOrDefault();
    public string UndoHint { get; private set; } = "Нет действий для отмены. Старые переносы доступны в журнале.";
    public event Action? Changed;
    public event Action<StorageTransfer>? Transferred;

    public void Enqueue(StoragePlan[] plans, int skipped, bool prepare)
    {
        if (plans.Length == 0) return;
        _pending.Enqueue(new StorageJob(plans, skipped, prepare));
        if (!_running) _ = Run();
        else Changed?.Invoke();
    }

    public void Resolve(QueueChoice choice) => _decision?.TrySetResult(choice);
    public void ResetUndo(string hint)
    {
        _undo.Clear();
        UndoHint = hint;
    }

    void RememberUndo(StorageUndo action)
    {
        _undo.Add(action);
        if (_undo.Count > 30) _undo.RemoveAt(0);
    }

    public void RememberRename(RenameStep[] steps)
    {
        if (steps.Length != 0) RememberUndo(new([], steps));
    }

    public void FinishUndo(StorageUndo action) => _undo.Remove(action);

    public void MarkUndone(Guid id)
    {
        foreach (var action in _undo)
            action.Moves.RemoveAll(move => move.Id == id);
        _undo.RemoveAll(action => action.Renames is null && action.Moves.Count == 0);
    }
    public void StopCurrent()
    {
        if (Active is { } job) job.Stop = true;
        Resolve(QueueChoice.Stop);
        Changed?.Invoke();
    }

    public void Retry(StorageJob previous)
    {
        if (previous.Retried || !_recent.Contains(previous)) return;
        var retry = previous.Errors.Where(row => row.Retry).Select(row => row.Plan with { Replacement = null }).ToArray();
        if (retry.Length == 0) return;
        previous.Retried = true;
        Enqueue(retry, 0, true);
    }

    async Task Run()
    {
        _running = true;
        while (_pending.TryDequeue(out var job)) {
            var moves = new List<StorageTransfer>();
            Active = job;
            Changed?.Invoke();
            foreach (var original in job.Plans) {
                if (job.Stop) break;
                var plan = original;
                bool countItem = true;
                job.Current = plan.Source.Name;
                Changed?.Invoke();
                try {
                    if (job.Prepare) {
                        var check = await Task.Run(() => StorageSelection.Check([plan], allowConflicts: true)[0]);
                        if (check.Error is { } error) throw new IOException(error);
                        plan = check.Plan;
                    }
                    if (job.Stop) { countItem = false; break; }
                    if (plan.Move && await Task.Run(() => StorageTransfers.InspectConflict(plan)) is { } conflict) {
                        if (job.Stop) { countItem = false; break; }
                        var decision = new TaskCompletionSource<QueueChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _decision = decision;
                        Conflict = (plan, conflict);
                        Changed?.Invoke();
                        var choice = await decision.Task;
                        _decision = null;
                        Conflict = null;
                        Changed?.Invoke();
                        if (choice == QueueChoice.Stop || job.Stop) { job.Stop = true; countItem = false; break; }
                        if (choice == QueueChoice.Skip) { job.Skipped++; continue; }
                        if (choice == QueueChoice.KeepBoth) plan = await Task.Run(() => StorageTransfers.KeepBoth(plan));
                        else plan = plan with { Replacement = conflict.Replacement };
                    }
                    var result = await Task.Run(() => transfers.Execute(plan));
                    if (result.Completed) {
                        job.Done++;
                        if (result.Move) moves.Add(result);
                        else {
                            moves.Clear();
                            ResetUndo("Копирование можно отменить вручную в папке назначения.");
                        }
                        Transferred?.Invoke(result);
                    }
                    else job.Errors.Add((plan, result.Error ?? result.State,
                        result.RecoveryPath is null && !result.HasReplacedBackup && result.State == "Ошибка"));
                }
                catch (Exception ex) { job.Errors.Add((plan, ex.Message, true)); }
                finally {
                    if (countItem) {
                        job.Finished++;
                        if (!original.Source.Folder) job.Remaining -= original.Source.Bytes;
                    }
                    Changed?.Invoke();
                }
            }
            if (moves.Count != 0) RememberUndo(new(moves));
            _recent.Insert(0, job);
            if (_recent.Count > 12) _recent.RemoveAt(_recent.Count - 1);
            Active = null;
            Changed?.Invoke();
        }
        _running = false;
    }
}
