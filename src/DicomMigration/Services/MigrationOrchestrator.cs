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

    /// <summary>
    /// Blocks until no phase is running, or the timeout expires. Returns true if it went idle.
    /// Used on shutdown so the process does not exit out from under an in-flight C-MOVE, which
    /// would strand rows in move_pending. Acquiring the phase gate is exactly "the phase finished",
    /// since RunHeldAsync releases it in its finally.
    /// </summary>
    public bool WaitForIdle(TimeSpan timeout)
    {
        if (!_gate.Wait(timeout))
            return false;

        _gate.Release();
        return true;
    }

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
    public bool StartVerifyStudy(string studyInstanceUid) =>
        TryStart("verify-study", ct => VerifyStudyBody(studyInstanceUid, ct));

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
            // "Completed" alone reads as success even when studies failed; say which it was.
            UpdateState(s => s.Message ??= s.Failed > 0 ? $"Completed with {s.Failed} failures" : "Completed");
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
                // Nothing is in flight once the phase returns; leaving stale entries would show
                // the dashboard a study that is no longer being worked on.
                s.InFlight = new List<ActiveOperation>();
                s.CurrentItem = null;
                s.ChunkIndex = 0;
                s.ChunkCount = 0;
                s.ChunkRange = null;
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
        lock (_stateLock)
        {
            mutate(_state);
            _state.Rev++;
            _state.LastProgressAtUtc = DateTime.UtcNow.ToString("o");
        }
    }

    // ---- Live activity tracking -------------------------------------------------------------

    /// <summary>
    /// Adds an in-flight operation and returns a handle that updates or removes it. Slots are
    /// keyed by StudyInstanceUID so concurrent movers each own their own entry instead of
    /// fighting over one CurrentItem string.
    /// </summary>
    private ActivitySlot BeginActivity(string studyUid, string? label, string operation, DicomNode peer)
    {
        var op = new ActiveOperation(
            studyUid, label, operation, peer.AeTitle, peer.Host, peer.Port,
            DateTime.UtcNow.ToString("o"), 0, -1, 0, null);

        UpdateState(s =>
        {
            s.InFlight = s.InFlight.Where(x => x.StudyInstanceUid != studyUid).Append(op).ToList();
            s.CurrentItem = studyUid; // kept for API compatibility
        });

        return new ActivitySlot(this, studyUid);
    }

    private void UpdateActivity(string studyUid, int completed, int remaining, int failed, long instanceDelta)
    {
        UpdateState(s =>
        {
            s.InFlight = s.InFlight
                .Select(x => x.StudyInstanceUid == studyUid
                    ? x with
                    {
                        SubOpsCompleted = completed,
                        SubOpsRemaining = remaining,
                        SubOpsFailed = failed,
                        LastProgressAtUtc = DateTime.UtcNow.ToString("o")
                    }
                    : x)
                .ToList();

            if (instanceDelta > 0) s.InstancesTransferred += instanceDelta;
        });
    }

    private void EndActivity(string studyUid)
    {
        UpdateState(s =>
        {
            s.InFlight = s.InFlight.Where(x => x.StudyInstanceUid != studyUid).ToList();
            if (s.CurrentItem == studyUid) s.CurrentItem = s.InFlight.FirstOrDefault()?.StudyInstanceUid;
        });
    }

    /// <summary>Scoped handle so a slot is always released, including on an exception.</summary>
    private sealed class ActivitySlot : IDisposable
    {
        private readonly MigrationOrchestrator _o;
        private readonly string _uid;
        private int _lastCompleted;

        public ActivitySlot(MigrationOrchestrator o, string uid) { _o = o; _uid = uid; }

        public void Report(int completed, int remaining, int failed)
        {
            var delta = completed - _lastCompleted;
            _lastCompleted = completed;
            _o.UpdateActivity(_uid, completed, remaining, failed, delta);
        }

        public void Dispose() => _o.EndActivity(_uid);
    }

    private static string? LabelFor(StudyRecord s) =>
        string.Join(" - ", new[] { s.PatientName, s.StudyDescription }
            .Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } l ? l : null;

    // ---- Phase 1: Inventory ----------------------------------------------------------------

    private async Task InventoryBody(InventoryFilter filter, CancellationToken ct)
    {
        var settings = _settings.Current;
        await _repo.LogEventAsync(null, "info", $"Inventory started against {settings.Source.AeTitle}");

        var studies = await _query.InventorySourceAsync(filter, ct, (index, count, range, running) =>
            UpdateState(s =>
            {
                s.ChunkIndex = index;
                s.ChunkCount = count;
                s.ChunkRange = range;
                // Querying, not yet persisting: report the running find total so the operator sees
                // the number climb instead of a static "0 of 0" for the whole query.
                s.Processed = running;
                s.Total = running;
            }));

        // The query phase used Processed/Total to show the find count climbing; reset both before
        // the persist loop starts incrementing, or it would count on top of the query total.
        UpdateState(s =>
        {
            s.Processed = 0;
            s.Succeeded = 0;
            s.Total = studies.Count;
            s.ChunkIndex = 0;
            s.ChunkCount = 0;
            s.ChunkRange = null;
        });

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

    /// <summary>
    /// Re-check one study against the destination without moving anything. Read-only, so it is safe
    /// to run repeatedly while waiting for an asynchronous destination to finish ingesting.
    /// </summary>
    private async Task VerifyStudyBody(string studyInstanceUid, CancellationToken ct)
    {
        var study = await _repo.GetAsync(studyInstanceUid)
            ?? throw new InvalidOperationException($"Study {studyInstanceUid} is not in the inventory.");

        var settings = _settings.Current;
        var level = ParseLevel(settings.Verify.Level);

        UpdateState(s => { s.Total = 1; s.CurrentItem = study.StudyInstanceUid; });

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

        // Clamp on read, not just on write: the API validates this key, but the installers seed the
        // settings table directly, so a value outside the accepted range can already be in the
        // database. Clamping here means such a value degrades to a safe pace rather than opening an
        // unbounded number of associations against a live clinical PACS.
        var parallelism = Math.Clamp(_settings.Current.Move.MaxParallelMoves, 1, SettingsStore.MaxParallelMovesCeiling);
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

        var settings = _settings.Current;
        using var slot = BeginActivity(study.StudyInstanceUid, LabelFor(study), "C-MOVE", settings.Destination);

        await _repo.SetStatusAsync(study.StudyInstanceUid, MigrationStatus.MovePending);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _move.MoveStudyAsync(study.StudyInstanceUid, ct,
            (completed, remaining, failed) => slot.Report(completed, remaining, failed));
        sw.Stop();

        var status = result.Success ? MigrationStatus.Moved : MigrationStatus.MoveFailed;
        // Only time successful moves: a fast rejection would otherwise look like blazing throughput.
        await _repo.RecordMoveAsync(study.StudyInstanceUid, status,
            result.Completed, result.Failed, result.Warnings, result.Error,
            result.Success ? sw.ElapsedMilliseconds : null);

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
    /// C-FIND the destination for each candidate study and compare counts at the configured level.
    ///
    /// Candidates are <c>moved</c> plus <c>mismatch</c> and <c>verify_failed</c>. Re-checking the
    /// two failure states matters because a destination that ingests asynchronously reports a study
    /// as missing until it finishes indexing, so verifying too soon marks studies mismatch even
    /// though the data arrived. Verification is a read-only C-FIND, so re-running it is free —
    /// whereas excluding these states forced a re-push to make a study verifiable again, which
    /// re-transmitted patient data to correct what was only ever a bookkeeping error.
    /// </summary>
    private async Task VerifyBody(CancellationToken ct)
    {
        var settings = _settings.Current;

        var candidates = new List<StudyRecord>();
        foreach (var status in new[] { MigrationStatus.Moved, MigrationStatus.Mismatch, MigrationStatus.VerifyFailed })
            candidates.AddRange(await _repo.GetByStatusAsync(status));

        var level = ParseLevel(settings.Verify.Level);

        UpdateState(s => s.Total = candidates.Count);
        await _repo.LogEventAsync(null, "info", $"Verification started ({level}): {candidates.Count} studies");

        foreach (var study in candidates)
        {
            ct.ThrowIfCancellationRequested();
            using var slot = BeginActivity(study.StudyInstanceUid, LabelFor(study),
                level == VerifyLevel.Instance ? "C-FIND IMAGE" : "C-FIND STUDY", settings.Destination);

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

        // For Instance-level verification, enumerate SOP UIDs directly rather than trusting the
        // NumberOfStudyRelatedInstances field, which some PACS leave empty.
        int? cmpDestInstances = destInstances;
        (bool ok, string? reason) result;

        if (level == VerifyLevel.Instance)
        {
            var srcUids = await _query.FindInstanceUidsAsync(settings.Source, study.StudyInstanceUid, ct);
            var dstUids = await _query.FindInstanceUidsAsync(settings.Destination, study.StudyInstanceUid, ct);
            cmpDestInstances = dstUids.Count;
            result = CompareInstanceUids(srcUids, dstUids);
        }
        else if (level == VerifyLevel.Series)
        {
            result = CompareCounts(study.SourceSeriesCount, destSeries, "series");
        }
        else
        {
            result = CompareCounts(study.SourceInstanceCount, destInstances, "instance");
        }

        var (match, reason) = result;
        var status = match ? MigrationStatus.Verified : MigrationStatus.Mismatch;
        var error = match
            ? null
            : $"{reason} (src={study.SourceInstanceCount}/{study.SourceSeriesCount} dest={cmpDestInstances ?? destInstances}/{destSeries})";

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
    /// Count comparison for Study and Series verification.
    ///
    /// A comparison in which either side was never measured is NOT a pass. The previous behaviour
    /// returned true for "destination has something and the source count is unknown", which is
    /// unsound: NumberOfStudyRelatedInstances / NumberOfStudyRelatedSeries are OPTIONAL C-FIND
    /// return keys, so a source that omits them left every study unmeasured — and three instances
    /// arriving out of five hundred scored as verified. A zero-to-zero comparison is likewise not
    /// evidence of anything. Both now surface as a mismatch with the reason stated, so an
    /// unverifiable study is visible rather than silently green.
    /// </summary>
    private static (bool ok, string? reason) CompareCounts(int? source, int? dest, string what)
    {
        if (!source.HasValue)
            return (false, $"source {what} count unavailable - cannot verify");
        if (source.Value == 0)
            return (false, $"source reported 0 {what}s - cannot verify");
        if (!dest.HasValue)
            return (false, $"destination {what} count unavailable - cannot verify");
        // Counts alone cannot prove containment, so a destination holding MORE than the source is
        // reported rather than passed — but it is called out separately, because it usually means
        // the destination already held data for this study rather than that the migration failed.
        // (Instance-level verification CAN prove containment, by UID, and does allow a superset.)
        if (dest.Value > source.Value)
            return (false, $"destination has more {what}s than source ({dest} > {source}) - cannot confirm by count");
        if (source.Value != dest.Value)
            return (false, $"{what} count mismatch");

        return (true, null);
    }

    /// <summary>
    /// Instance-level comparison by SOP Instance UID. Compares identity rather than magnitude:
    /// equal counts do not prove the destination holds the same instances, and an empty source
    /// enumeration proves nothing at all.
    /// </summary>
    private static (bool ok, string? reason) CompareInstanceUids(IReadOnlySet<string> source, IReadOnlySet<string> dest)
    {
        if (source.Count == 0)
            return (false, "source returned 0 instances - cannot verify");

        var missing = source.Count(uid => !dest.Contains(uid));
        if (missing > 0)
            return (false, $"{missing} of {source.Count} instances missing on destination");

        return (true, null);
    }

    private static VerifyLevel ParseLevel(string level) =>
        Enum.TryParse<VerifyLevel>(level, ignoreCase: true, out var parsed) ? parsed : VerifyLevel.Study;
}
