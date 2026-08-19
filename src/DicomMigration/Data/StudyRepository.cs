using Dapper;
using DicomMigration.Models;

namespace DicomMigration.Data;

/// <summary>
/// All persistence for the inventory and migration state. Every write stamps updated_at.
/// </summary>
public sealed class StudyRepository
{
    private readonly Database _db;

    public StudyRepository(Database db) => _db = db;

    private static string Now() => DateTime.UtcNow.ToString("o");

    /// <summary>
    /// Insert a freshly inventoried study, or refresh its source-side metadata if it already
    /// exists. Existing migration status/counts are left untouched so re-running inventory is safe.
    /// </summary>
    public async Task UpsertInventoryAsync(StudyRecord s)
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"INSERT INTO studies
                (study_instance_uid, patient_id, patient_name, study_date, study_description,
                 accession_number, modalities, source_series_count, source_instance_count,
                 status, inventoried_at, updated_at)
              VALUES
                (@StudyInstanceUid, @PatientId, @PatientName, @StudyDate, @StudyDescription,
                 @AccessionNumber, @Modalities, @SourceSeriesCount, @SourceInstanceCount,
                 'inventoried', @Now, @Now)
              ON CONFLICT(study_instance_uid) DO UPDATE SET
                 patient_id            = excluded.patient_id,
                 patient_name          = excluded.patient_name,
                 study_date            = excluded.study_date,
                 study_description     = excluded.study_description,
                 accession_number      = excluded.accession_number,
                 modalities            = excluded.modalities,
                 -- COALESCE so a later inventory pass cannot blank a count we already have.
                 -- These come from OPTIONAL C-FIND keys the source may omit on any given query;
                 -- overwriting a known count with NULL would make the study unverifiable, and
                 -- verification treats an unknown source count as a failure to prove anything.
                 source_series_count   = COALESCE(excluded.source_series_count, source_series_count),
                 source_instance_count = COALESCE(excluded.source_instance_count, source_instance_count),
                 updated_at            = excluded.updated_at;",
            new
            {
                s.StudyInstanceUid, s.PatientId, s.PatientName, s.StudyDate, s.StudyDescription,
                s.AccessionNumber, s.Modalities, s.SourceSeriesCount, s.SourceInstanceCount,
                Now = Now()
            });
    }

    public async Task<IReadOnlyList<StudyRecord>> GetByStatusAsync(string status, int? limit = null)
    {
        using var conn = _db.Open();
        var sql = "SELECT * FROM studies WHERE status = @status ORDER BY study_date";
        if (limit is > 0) sql += " LIMIT " + limit.Value;
        var rows = await conn.QueryAsync<StudyRecord>(sql, new { status });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<StudyRecord>> GetPageAsync(string? status, int limit, int offset)
    {
        using var conn = _db.Open();
        var sql = status is null
            ? "SELECT * FROM studies ORDER BY updated_at DESC LIMIT @limit OFFSET @offset"
            : "SELECT * FROM studies WHERE status = @status ORDER BY updated_at DESC LIMIT @limit OFFSET @offset";
        var rows = await conn.QueryAsync<StudyRecord>(sql, new { status, limit, offset });
        return rows.ToList();
    }

    public async Task<StudyRecord?> GetAsync(string studyInstanceUid)
    {
        using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<StudyRecord>(
            "SELECT * FROM studies WHERE study_instance_uid = @studyInstanceUid",
            new { studyInstanceUid });
    }

    /// <summary>Status counts for the dashboard summary.</summary>
    public async Task<Dictionary<string, int>> GetStatusCountsAsync()
    {
        using var conn = _db.Open();
        var rows = await conn.QueryAsync<(string status, int count)>(
            "SELECT status, COUNT(*) AS count FROM studies GROUP BY status");
        return rows.ToDictionary(r => r.status, r => r.count);
    }

    public async Task SetStatusAsync(string studyUid, string status, string? error = null)
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync(
            "UPDATE studies SET status = @status, last_error = @error, updated_at = @now WHERE study_instance_uid = @studyUid",
            new { studyUid, status, error, now = Now() });
    }

    public async Task RecordMoveAsync(string studyUid, string status, int completed, int failed, int warnings, string? error, long? moveMs = null)
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"UPDATE studies SET
                status            = @status,
                move_completed    = @completed,
                move_failed_count = @failed,
                move_warnings     = @warnings,
                last_error        = @error,
                attempts          = attempts + 1,
                move_ms           = COALESCE(@moveMs, move_ms),
                moved_at          = @now,
                updated_at        = @now
              WHERE study_instance_uid = @studyUid;",
            new { studyUid, status, completed, failed, warnings, error, moveMs, now = Now() });
    }

    /// <summary>
    /// Throughput and remaining work, for the completion estimate.
    ///
    /// Rate is measured in INSTANCES per second, not studies per minute: study sizes in this
    /// archive span 1 to over 1,100 instances, so a studies-based estimate is dominated by
    /// whichever sizes happen to come first and is wildly wrong either way. Only successful moves
    /// with a recorded duration contribute, so a fast failure cannot inflate the rate.
    /// </summary>
    public async Task<MigrationEstimate> GetEstimateAsync()
    {
        using var conn = _db.Open();

        var done = await conn.QuerySingleAsync<(int Studies, long Instances, long Ms)>(
            @"SELECT COUNT(*)                              AS Studies,
                     COALESCE(SUM(move_completed), 0)      AS Instances,
                     COALESCE(SUM(move_ms), 0)             AS Ms
              FROM studies
              WHERE move_ms IS NOT NULL AND move_ms > 0 AND move_completed > 0;");

        var pending = await conn.QuerySingleAsync<(int Studies, long Instances, int Unknown)>(
            @"SELECT COUNT(*)                                        AS Studies,
                     COALESCE(SUM(source_instance_count), 0)         AS Instances,
                     SUM(CASE WHEN source_instance_count IS NULL THEN 1 ELSE 0 END) AS Unknown
              FROM studies
              WHERE status IN ('inventoried', 'move_failed');");

        return new MigrationEstimate(
            done.Studies, done.Instances, done.Ms,
            pending.Studies, pending.Instances, pending.Unknown);
    }

    public async Task RecordVerifyAsync(string studyUid, string status, int? destSeries, int? destInstances, string? error)
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"UPDATE studies SET
                status             = @status,
                dest_series_count  = @destSeries,
                dest_instance_count = @destInstances,
                last_error         = @error,
                verified_at        = @now,
                updated_at         = @now
              WHERE study_instance_uid = @studyUid;",
            new { studyUid, status, destSeries, destInstances, error, now = Now() });
    }

    /// <summary>
    /// Zero the attempt counter for the given studies so a manual retry isn't blocked by
    /// MaxAttempts. Called before re-running C-MOVE on previously failed studies.
    /// </summary>
    public async Task<int> ResetAttemptsAsync(IEnumerable<string> studyUids)
    {
        var ids = studyUids.ToList();
        if (ids.Count == 0) return 0;
        using var conn = _db.Open();
        return await conn.ExecuteAsync(
            "UPDATE studies SET attempts = 0, last_error = NULL, updated_at = @now WHERE study_instance_uid IN @ids",
            new { ids, now = Now() });
    }

    /// <summary>
    /// Reclaims studies left in <c>move_pending</c> by a process that stopped mid-move.
    ///
    /// That status is written before the C-MOVE and rewritten after it returns, so an interrupted
    /// process (a crash, or an installer stopping the service to upgrade binaries) leaves rows
    /// stranded: no phase selects <c>move_pending</c>, so migrate, retry and verify would all skip
    /// them forever and the dashboard would show them as neither failed nor outstanding. They are
    /// moved to <c>move_failed</c> so Retry picks them up, and attempts is reset so the retry cap
    /// does not immediately exclude them for an interruption that was not their fault.
    /// </summary>
    public async Task<int> ReclaimInterruptedMovesAsync()
    {
        using var conn = _db.Open();
        return await conn.ExecuteAsync(
            "UPDATE studies SET status = @failed, attempts = 0, " +
            "last_error = 'Interrupted: service stopped mid-move', updated_at = @now " +
            "WHERE status = @pending",
            new { failed = MigrationStatus.MoveFailed, pending = MigrationStatus.MovePending, now = Now() });
    }

    /// <summary>
    /// Clears the inventory and history so a new migration can start, and returns the path of the
    /// backup taken first.
    ///
    /// Deliberately keeps the settings table: the AE configuration, throttle and verify level are
    /// how this deployment is set up, not part of any one migration run. Point it at a new source
    /// or destination from the Connections panel afterwards.
    ///
    /// A backup is taken before anything is deleted. This wipes the only record of which studies
    /// were migrated and verified -- if a question comes up later about a completed migration, that
    /// file is the answer -- so it is copied rather than trusted to a click.
    /// </summary>
    public async Task<string> ResetAsync()
    {
        var backupPath = _db.BackupTo(Path.Combine(
            Path.GetDirectoryName(_db.FilePath)!, "archive"));

        using (var conn = _db.Open())
        {
            using (var tx = conn.BeginTransaction())
            {
                await conn.ExecuteAsync("DELETE FROM studies;", transaction: tx);
                await conn.ExecuteAsync("DELETE FROM event_log;", transaction: tx);
                // event_log ids restart from 1 rather than continuing from the old run.
                await conn.ExecuteAsync(
                    "DELETE FROM sqlite_sequence WHERE name = event_log;", transaction: tx);
                tx.Commit();
            }

            // Outside the transaction: VACUUM cannot run inside one. Reclaims the space a large
            // inventory occupied instead of leaving a multi-hundred-MB file of free pages.
            await conn.ExecuteAsync("VACUUM;");
        }

        await LogEventAsync(null, "warn",
            $"Migration database cleared for a new run; previous data backed up to {Path.GetFileName(backupPath)}");

        return backupPath;
    }

    public async Task LogEventAsync(string? studyUid, string level, string message)
    {
        using var conn = _db.Open();
        await conn.ExecuteAsync(
            "INSERT INTO event_log (study_instance_uid, level, message, created_at) VALUES (@studyUid, @level, @message, @now)",
            new { studyUid, level, message, now = Now() });
    }

    public async Task<IReadOnlyList<dynamic>> GetRecentEventsAsync(int limit = 100)
    {
        using var conn = _db.Open();
        var rows = await conn.QueryAsync(
            "SELECT study_instance_uid, level, message, created_at FROM event_log ORDER BY id DESC LIMIT @limit",
            new { limit });
        return rows.ToList();
    }
}
