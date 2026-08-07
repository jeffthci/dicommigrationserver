using DicomMigration.Configuration;
using DicomMigration.Data;
using DicomMigration.Dicom;
using DicomMigration.Models;

namespace DicomMigration.Services;

/// <summary>
/// Drives the migration phases. Only one phase runs at a time (guarded by a semaphore acquired
/// synchronously when a phase is started, so the busy-check is atomic). Progress is exposed via
/// <see cref="State"/>. Throttle settings (parallelism, inter-move delay) are read live from
/// <see cref="SettingsStore"/>.
/// </summary>
public sealed class MigrationOrchestrator
{
    private readonly DicomQueryService _query;
    private readonly DicomMoveService _move;
    private readonly StudyRepository _repo;
    private readonly SettingsStore _settings;
    private readonly ILogger<MigrationOrchestrator> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private MigrationState _state = new();
    private CancellationTokenSource? _cts; // guarded by _stateLock

    public MigrationOrchestrator(
        DicomQueryService query,
        DicomMoveService move,
        StudyRepository repo,
        SettingsStore settings,
        ILogger<MigrationOrchestrator> logger)
    {
        _query = query;
        _move = move;
        _repo = repo;
        _settings = settings;
        _logger = logger;
    }

    public MigrationState State
    {
        get { lock (_stateLock) return _state.Clone(); }
    }

    public bool IsBusy => _gate.CurrentCount == 0;

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_stateLock) cts = _cts;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { /* phase finished between read and cancel */ }
    }

    // ---- Public entry points: return false if a phase is already running -------------------

    public bool StartInventory(InventoryFilter filter) => TryStart("inventory", ct => InventoryBody(filter, ct));
    public bool StartMigrate() => TryStart("migrate", MigrateBody);
    public bool StartRetry() => TryStart("retry", RetryBody);
    public bool StartVerify() => TryStart("verify", VerifyBody);
    public bool StartMoveStudy(string studyInstanceUid) =>
        TryStart("move-study", ct => MoveStudyBody(studyInstanceUid, ct));

    /// <summary>
    /// Acquire the single-run gate synchronously; if free, set up state + cancellation and run the
    /// phase body on a background task. Returns false (without starting anything) if busy, so the
    /// API can answer 409 with no unobserved background fault.
    /// </summary>
    private bool TryStart(string phase, Func<CancellationToken, Task> body)
    {
        if (!_gate.Wait(0))
            return false;

        var cts = new CancellationTokenSource();
        lock (_stateLock)
        {
            _cts = cts;
            _state = new MigrationState
            {
                Phase = phase,
                IsRunning = true,
                StartedAt = DateTime.UtcNow.ToString("o")
            };
        }

        _ = Task.Run(() => RunHeldAsync(phase, body, cts));
        return true;
    }

    /// <summary>Runs a phase body with the gate already held; releases it (and the CTS) in finally.</summary>
    private async Task RunHeldAsync(string phase, Func<CancellationToken, Task> body, CancellationTokenSource cts)
    {
        try
        {
            await body(cts.Token);
            UpdateState(s => s.Message ??= "Completed");
        }
        catch (OperationCanceledException)
        {
            UpdateState(s => s.Message = "Cancelled");
            _logger.LogWarning("{Phase} cancelled", phase);
        }
        catch (Exception ex)
        {
            UpdateState(s => s.Message = "Error: " + ex.Message);
            _logger.LogError(ex, "{Phase} failed", phase);
        }
        finally
        {
            UpdateState(s =>
            {
                s.IsRunning = false;
                s.FinishedAt = DateTime.UtcNow.ToString("o");
            });
            lock (_stateLock)
            {
                if (ReferenceEquals(_cts, cts)) _cts = null;
            }
            cts.Dispose();
            _gate.Release();
        }
    }

    private void UpdateState(Action<MigrationState> mutate)
    {
        lock (_stateLock) mutate(_state);
    }

    // ---- Phase 1: Inventory ----------------------------------------------------------------

    private async Task InventoryBody(InventoryFilter filter, CancellationToken ct)
    {
        var settings = _settings.Current;
        await _repo.LogEventAsync(null, "info", $"Inventory started against {settings.Source.AeTitle}");
        var studies = await _query.InventorySourceAsync(filter, ct);

        UpdateState(s => s.Total = studies.Count);

        foreach (var study in studies)
        {
            ct.ThrowIfCancellationRequested();

            if (settings.Inventory.FetchSeriesCounts && study.SourceSeriesCount is null)
            {
                var (_, series, instances) = await _query.FindStudyAsync(settings.Source, study.StudyInstanceUid, ct);
                study.SourceSeriesCount ??= series;
                study.SourceInstanceCount ??= instances;
            }

            await _repo.UpsertInventoryAsync(study);
            UpdateState(s =>
            {
                s.Processed++;
                s.Succeeded++;
                s.CurrentItem = study.StudyInstanceUid;
            });
        }

        await _repo.LogEventAsync(null, "info", $"Inventory complete: {studies.Count} studies recorded");
    }

    // ---- Phase 2: Migrate ------------------------------------------------------------------

    /// <summary>
    /// C-MOVE every study that is inventoried or previously failed (under the retry cap).
    /// Runs sequentially or with limited parallelism per the live MaxParallelMoves setting.
    /// </summary>
    private async Task MigrateBody(CancellationToken ct)
    {
        var maxAttempts = _settings.Current.Move.MaxAttempts;
        var pending = (await _repo.GetByStatusAsync(MigrationStatus.Inventoried)).ToList();
        var retryable = (await _repo.GetByStatusAsync(MigrationStatus.MoveFailed))
            .Where(s => s.Attempts < maxAttempts);
        pending.AddRange(retryable);

        await _repo.LogEventAsync(null, "info", $"Migration started: {pending.Count} studies to move");
        await MoveAllAsync(pending, ct);
        await _repo.LogEventAsync(null, "info", "Migration phase complete");
    }

    // ---- Single study: operator-initiated push ----------------------------------------------

    /// <summary>
    /// C-MOVE one study on demand, whatever its current status. Goes through the same phase gate
    /// as the bulk phases so it cannot run concurrently with a migration and fight for the source's
    /// association limit.
    ///
    /// The attempt counter is reset first: an explicit push is an operator decision and should not
    /// be silently skipped because the study already burned through MaxAttempts.
    /// </summary>
    private async Task MoveStudyBody(string studyInstanceUid, CancellationToken ct)
    {
        var study = await _repo.GetAsync(studyInstanceUid)
            ?? throw new InvalidOperationException($"Study {studyInstanceUid} is not in the inventory.");

        UpdateState(s => s.Total = 1);
        await _repo.ResetAttemptsAsync(new[] { study.StudyInstanceUid });
        await _repo.LogEventAsync(study.StudyInstanceUid, "info", "Manual push started");

        await MoveOneAsync(study, ct);
    }

    // ---- Retry: re-move studies that previously failed -------------------------------------

    /// <summary>
    /// Manually retry every <c>move_failed</c> study, regardless of the attempt cap. Attempt
    /// counters are reset first so MaxAttempts doesn't immediately block them again.
    /// </summary>
    private async Task RetryBody(CancellationToken ct)
    {
        var failed = (await _repo.GetByStatusAsync(MigrationStatus.MoveFailed)).ToList();
        await _repo.ResetAttemptsAsync(failed.Select(f => f.StudyInstanceUid));
        await _repo.LogEventAsync(null, "info", $"Retry started: re-moving {failed.Count} failed studies");
        await MoveAllAsync(failed, ct);
        await _repo.LogEventAsync(null, "info", "Retry phase complete");
    }

    /// <summary>
    /// Shared move driver for the migrate and retry phases: applies the live parallelism limit
    /// and per-study throttle delay while C-MOVEing each study.
    /// </summary>
    private async Task MoveAllAsync(IReadOnlyList<StudyRecord> studies, CancellationToken ct)
    {
        UpdateState(s => s.Total = studies.Count);

        var parallelism = Math.Max(1, _settings.Current.Move.MaxParallelMoves);
        using var throttle = new SemaphoreSlim(parallelism, parallelism);

        var tasks = studies.Select(async study =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                await MoveOneAsync(study, ct);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task MoveOneAsync(StudyRecord study, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        UpdateState(s => s.CurrentItem = study.StudyInstanceUid);

        await _repo.SetStatusAsync(study.StudyInstanceUid, MigrationStatus.MovePending);
        var result = await _move.MoveStudyAsync(study.StudyInstanceUid, ct);

        var status = result.Success ? MigrationStatus.Moved : MigrationStatus.MoveFailed;
        await _repo.RecordMoveAsync(study.StudyInstanceUid, status,
            result.Completed, result.Failed, result.Warnings, result.Error);

        await _repo.LogEventAsync(study.StudyInstanceUid,
            result.Success ? "info" : "error",
            result.Success
                ? $"Moved: {result.Completed} instances"
                : $"Move failed: {result.Error}");

        UpdateState(s =>
        {
            s.Processed++;
            if (result.Success) s.Succeeded++; else s.Failed++;
        });

        // Throttle: read the delay live so the pace can be tuned mid-migration.
        var delay = _settings.Current.Move.DelayBetweenMovesMs;
        if (delay > 0)
            await Task.Delay(delay, ct);
    }

    // ---- Phase 3: Verify -------------------------------------------------------------------

    /// <summary>
    /// For every moved study, C-FIND the destination and compare counts at the configured level.
    /// </summary>
    private async Task VerifyBody(CancellationToken ct)
    {
        var settings = _settings.Current;
        var moved = await _repo.GetByStatusAsync(MigrationStatus.Moved);
        var level = ParseLevel(settings.Verify.Level);

        UpdateState(s => s.Total = moved.Count);
        await _repo.LogEventAsync(null, "info", $"Verification started ({level}): {moved.Count} studies");

        foreach (var study in moved)
        {
            ct.ThrowIfCancellationRequested();
            UpdateState(s => s.CurrentItem = study.StudyInstanceUid);

            try
            {
                await VerifyOneAsync(study, settings, level, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _repo.RecordVerifyAsync(study.StudyInstanceUid, MigrationStatus.VerifyFailed, null, null, ex.Message);
                await _repo.LogEventAsync(study.StudyInstanceUid, "error", $"Verify error: {ex.Message}");
                UpdateState(s => { s.Processed++; s.Failed++; });
            }
        }

        await _repo.LogEventAsync(null, "info", "Verification phase complete");
    }

    private async Task VerifyOneAsync(StudyRecord study, DicomSettings settings, VerifyLevel level, CancellationToken ct)
    {
        var (found, destSeries, destInstances) = await _query.FindStudyAsync(settings.Destination, study.StudyInstanceUid, ct);

        if (!found)
        {
            await _repo.RecordVerifyAsync(study.StudyInstanceUid, MigrationStatus.Mismatch, null, null, "Study not found on destination");
            await _repo.LogEventAsync(study.StudyInstanceUid, "warn", "Verify mismatch: not found on destination");
            UpdateState(s => { s.Processed++; s.Failed++; });
            return;
        }

        // For Instance-level verification, count SOP UIDs directly rather than trusting the
        // NumberOfStudyRelatedInstances field, which some PACS leave empty.
        int? srcInstances = study.SourceInstanceCount;
        int? cmpDestInstances = destInstances;
        if (level == VerifyLevel.Instance)
        {
            srcInstances = await _query.CountInstancesAsync(settings.Source, study.StudyInstanceUid, ct);
            cmpDestInstances = await _query.CountInstancesAsync(settings.Destination, study.StudyInstanceUid, ct);
        }

        bool match = level switch
        {
            VerifyLevel.Series => destSeries.HasValue && study.SourceSeriesCount.HasValue && destSeries == study.SourceSeriesCount,
            VerifyLevel.Instance => srcInstances.HasValue && cmpDestInstances.HasValue && srcInstances == cmpDestInstances,
            _ => CompareStudyCounts(study.SourceInstanceCount, destInstances)
        };

        var status = match ? MigrationStatus.Verified : MigrationStatus.Mismatch;
        var error = match ? null : $"src={study.SourceInstanceCount}/{study.SourceSeriesCount} dest={cmpDestInstances ?? destInstances}/{destSeries}";

        await _repo.RecordVerifyAsync(study.StudyInstanceUid, status, destSeries, cmpDestInstances ?? destInstances, error);
        await _repo.LogEventAsync(study.StudyInstanceUid, match ? "info" : "warn",
            match ? "Verified" : $"Verify mismatch: {error}");

        UpdateState(s =>
        {
            s.Processed++;
            if (match) s.Succeeded++; else s.Failed++;
        });
    }

    /// <summary>
    /// Study-level comparison. If the source never reported an instance count we can't prove
    /// equality, so treat "destination has data and source count unknown" as a best-effort pass.
    /// </summary>
    private static bool CompareStudyCounts(int? source, int? dest)
    {
        if (source.HasValue && dest.HasValue)
            return source == dest;
        return dest is > 0 && source is null;
    }

    private static VerifyLevel ParseLevel(string level) =>
        Enum.TryParse<VerifyLevel>(level, ignoreCase: true, out var parsed) ? parsed : VerifyLevel.Study;
}
