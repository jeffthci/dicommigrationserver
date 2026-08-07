using DicomMigration.Configuration;
using DicomMigration.Models;
using DicomMigration.Services;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;

namespace DicomMigration.Dicom;

/// <summary>
/// C-FIND queries against a remote PACS: full inventory of the source, and
/// per-study lookups against the destination for verification.
/// </summary>
public sealed class DicomQueryService
{
    /// <summary>
    /// Wall-clock ceiling for the dashboard's C-ECHO test. Deliberately short: this backs a
    /// button an operator is watching, unlike a migration where a long association timeout is fine.
    /// </summary>
    private const int TestTimeoutSeconds = 15;

    private readonly IDicomClientFactory _clientFactory;
    private readonly SettingsStore _settings;
    private readonly ILogger<DicomQueryService> _logger;

    public DicomQueryService(
        IDicomClientFactory clientFactory,
        SettingsStore settings,
        ILogger<DicomQueryService> logger)
    {
        _clientFactory = clientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Holds the final (non-Pending) DIMSE status of a C-FIND so it can be inspected after the
    /// association closes. Without this a refusal is indistinguishable from "no matches": the
    /// response handler simply never fires with a dataset and the query looks like an empty
    /// archive. A source PACS that has not authorized our AE for query/retrieve returns
    /// 0xA700 Refused on every C-FIND while still accepting the association, so this is the
    /// difference between a clear error and a silently empty inventory.
    /// </summary>
    private sealed class FinalStatus
    {
        public DicomStatus? Value;
    }

    /// <summary>
    /// Throws if a C-FIND did not finish successfully. Cancel is treated as benign because it is
    /// the caller's own doing; anything else is a real failure that must not be reported as
    /// zero results.
    /// </summary>
    private static void EnsureFindSucceeded(FinalStatus final, DicomNode node, string operation)
    {
        var status = final.Value;

        if (status is null)
            throw new InvalidOperationException(
                $"{operation} against {node.AeTitle}@{node.Host}:{node.Port} ended without a final response.");

        if (status.State is DicomState.Success or DicomState.Cancel)
            return;

        var hint = status.Code == 0xa700
            ? $" This usually means AE '{node.AeTitle}' has not authorized calling AE for query/retrieve (Verification/C-ECHO can still succeed)."
            : string.Empty;

        throw new InvalidOperationException(
            $"{operation} refused by {node.AeTitle}@{node.Host}:{node.Port}: {status}.{hint}");
    }

    private IDicomClient CreateClient(DicomNode node, DicomSettings settings)
    {
        var client = _clientFactory.Create(node.Host, node.Port, node.UseTls, settings.LocalAeTitle, node.AeTitle);
        client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(settings.Move.AssociationTimeoutSeconds);
        client.NegotiateAsyncOps();
        return client;
    }

    /// <summary>
    /// C-ECHO against an arbitrary node, used by the dashboard's connection test. The node is
    /// passed in rather than read from settings so the dashboard can test edited-but-unsaved
    /// values.
    ///
    /// A failed echo is an ordinary outcome, so connection/timeout/rejection errors are caught
    /// and returned as <see cref="EchoResult"/> rather than thrown. Note that a successful echo
    /// only proves the remote accepts Verification from this calling AE — PACS commonly authorize
    /// C-FIND and C-MOVE separately, so this passing does not guarantee a migration will work.
    /// </summary>
    public async Task<EchoResult> EchoAsync(DicomNode node, CancellationToken ct)
    {
        var settings = _settings.Current;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Move.AssociationTimeoutSeconds bounds requests on an established association, not the
        // TCP connect. Against an unroutable host the connect sits until the OS gives up (minutes),
        // which is far too long for a button in the UI, so bound the whole test separately.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(TestTimeoutSeconds));

        try
        {
            var client = CreateClient(node, settings);

            DicomStatus? status = null;
            var request = new DicomCEchoRequest();
            request.OnResponseReceived += (req, response) => status = response.Status;

            await client.AddRequestAsync(request);

            // Cancellation alone is not enough: fo-dicom cannot always abort a connect already
            // in progress. Race the send against the deadline so the caller always gets a prompt
            // answer, and observe the abandoned task's exception so it is not left unobserved.
            var sendTask = client.SendAsync(timeoutCts.Token);
            var finished = await Task.WhenAny(sendTask, Task.Delay(Timeout.Infinite, timeoutCts.Token));

            if (finished != sendTask)
            {
                _ = sendTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                sw.Stop();

                ct.ThrowIfCancellationRequested(); // caller cancelled, not our deadline

                _logger.LogWarning("C-ECHO to {Ae}@{Host}:{Port} timed out after {Sec}s",
                    node.AeTitle, node.Host, node.Port, TestTimeoutSeconds);
                return EchoResult.Failure(
                    $"Timed out after {TestTimeoutSeconds}s with no response from {node.Host}:{node.Port}. " +
                    "Check the host and port, and that a firewall is not dropping the connection.",
                    sw.ElapsedMilliseconds);
            }

            await sendTask;
            sw.Stop();

            if (status is null)
            {
                _logger.LogWarning("C-ECHO to {Ae}@{Host}:{Port} completed with no response",
                    node.AeTitle, node.Host, node.Port);
                return EchoResult.Failure("Association completed but no C-ECHO response was received.", sw.ElapsedMilliseconds);
            }

            if (status.State == DicomState.Success)
            {
                _logger.LogInformation("C-ECHO to {Ae}@{Host}:{Port} succeeded in {Ms}ms",
                    node.AeTitle, node.Host, node.Port, sw.ElapsedMilliseconds);
                return EchoResult.Success(sw.ElapsedMilliseconds);
            }

            _logger.LogWarning("C-ECHO to {Ae}@{Host}:{Port} returned {Status}",
                node.AeTitle, node.Host, node.Port, status);
            return EchoResult.Failure("Remote returned status: " + status, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "C-ECHO to {Ae}@{Host}:{Port} failed",
                node.AeTitle, node.Host, node.Port);

            // Association rejection is the common, actionable failure: it usually means the
            // remote does not know this calling AE Title, or does not accept it from our IP.
            var detail = ex is DicomAssociationRejectedException rej
                ? $"Association rejected by {node.AeTitle} (reason: {rej.RejectReason}, source: {rej.RejectSource}). Check that AE '{settings.LocalAeTitle}' and this server's IP are registered on the remote."
                : ex.Message;

            return EchoResult.Failure(detail, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// STUDY-level C-FIND against the source. Returns one record per study with patient
    /// metadata and source-side series/instance counts.
    /// </summary>
    public async Task<IReadOnlyList<StudyRecord>> InventorySourceAsync(InventoryFilter filter, CancellationToken ct)
    {
        var settings = _settings.Current;
        var chunks = DateChunker.Split(filter.StudyDateRange, settings.Inventory.ChunkDays);

        if (chunks.Count <= 1)
            return await InventoryChunkAsync(filter, filter.StudyDateRange, ct);

        // Chunked: one short association per date window, paced by DelayBetweenChunksMs, so a
        // large archive is walked in steps instead of held open in a single long query.
        var all = new List<StudyRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var delay = settings.Inventory.DelayBetweenChunksMs;

        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var chunk = await InventoryChunkAsync(filter, chunks[i], ct);

            // A study dated on a chunk boundary can appear in two windows; keep the first.
            foreach (var s in chunk)
                if (seen.Add(s.StudyInstanceUid))
                    all.Add(s);

            _logger.LogInformation("Inventory chunk {N}/{Total} ({Range}): {Count} studies, {Running} total",
                i + 1, chunks.Count, chunks[i], chunk.Count, all.Count);

            if (delay > 0 && i < chunks.Count - 1)
                await Task.Delay(delay, ct);
        }

        return all;
    }

    private async Task<IReadOnlyList<StudyRecord>> InventoryChunkAsync(
        InventoryFilter filter, string? studyDateRange, CancellationToken ct)
    {
        var settings = _settings.Current;
        var results = new List<StudyRecord>();

        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);
        var ds = request.Dataset;
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, "");
        ds.AddOrUpdate(DicomTag.PatientID, filter.PatientId ?? "");
        ds.AddOrUpdate(DicomTag.PatientName, "");
        ds.AddOrUpdate(DicomTag.StudyDate, studyDateRange ?? "");
        ds.AddOrUpdate(DicomTag.StudyTime, "");
        ds.AddOrUpdate(DicomTag.StudyDescription, "");
        ds.AddOrUpdate(DicomTag.AccessionNumber, filter.AccessionNumber ?? "");
        ds.AddOrUpdate(DicomTag.ModalitiesInStudy, filter.ModalitiesInStudy ?? "");
        ds.AddOrUpdate(DicomTag.NumberOfStudyRelatedSeries, "");
        ds.AddOrUpdate(DicomTag.NumberOfStudyRelatedInstances, "");

        var final = new FinalStatus();
        request.OnResponseReceived += (req, response) =>
        {
            if (response.Status != DicomStatus.Pending)
                final.Value = response.Status;

            if (response.Status != DicomStatus.Pending || response.Dataset is null)
                return;

            var uid = response.Dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "");
            if (string.IsNullOrWhiteSpace(uid))
                return;

            results.Add(new StudyRecord
            {
                StudyInstanceUid = uid,
                PatientId = response.Dataset.GetSingleValueOrDefault(DicomTag.PatientID, (string?)null),
                PatientName = response.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, (string?)null),
                StudyDate = response.Dataset.GetSingleValueOrDefault(DicomTag.StudyDate, (string?)null),
                StudyDescription = response.Dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, (string?)null),
                AccessionNumber = response.Dataset.GetSingleValueOrDefault(DicomTag.AccessionNumber, (string?)null),
                Modalities = JoinModalities(response.Dataset),
                SourceSeriesCount = GetInt(response.Dataset, DicomTag.NumberOfStudyRelatedSeries),
                SourceInstanceCount = GetInt(response.Dataset, DicomTag.NumberOfStudyRelatedInstances)
            });
        };

        var client = CreateClient(settings.Source, settings);
        await client.AddRequestAsync(request);
        await client.SendAsync(ct);
        EnsureFindSucceeded(final, settings.Source, "Inventory C-FIND");

        _logger.LogInformation("Inventory C-FIND returned {Count} studies from {Ae}", results.Count, settings.Source.AeTitle);
        return results;
    }

    /// <summary>
    /// STUDY-level C-FIND for a single StudyInstanceUID. Used to confirm a study landed on
    /// the destination and to read its instance/series counts.
    /// </summary>
    public async Task<(bool found, int? seriesCount, int? instanceCount)> FindStudyAsync(
        DicomNode node, string studyInstanceUid, CancellationToken ct)
    {
        var settings = _settings.Current;
        bool found = false;
        int? series = null, instances = null;

        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);
        request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyInstanceUid);
        request.Dataset.AddOrUpdate(DicomTag.NumberOfStudyRelatedSeries, "");
        request.Dataset.AddOrUpdate(DicomTag.NumberOfStudyRelatedInstances, "");

        var final = new FinalStatus();
        request.OnResponseReceived += (req, response) =>
        {
            if (response.Status != DicomStatus.Pending)
                final.Value = response.Status;

            if (response.Status != DicomStatus.Pending || response.Dataset is null)
                return;
            found = true;
            series = GetInt(response.Dataset, DicomTag.NumberOfStudyRelatedSeries);
            instances = GetInt(response.Dataset, DicomTag.NumberOfStudyRelatedInstances);
        };

        var client = CreateClient(node, settings);
        await client.AddRequestAsync(request);
        await client.SendAsync(ct);
        // Critical for verification: a refused query must not be reported as "study not found",
        // which would look like a migration failure rather than a permissions problem.
        EnsureFindSucceeded(final, node, "Verification C-FIND");
        return (found, series, instances);
    }

    /// <summary>
    /// IMAGE-level C-FIND that counts SOP Instance UIDs for a study. Definitive but heavier;
    /// used for Instance-level verification when NumberOfStudyRelatedInstances is unreliable.
    /// </summary>
    public async Task<int> CountInstancesAsync(DicomNode node, string studyInstanceUid, CancellationToken ct)
    {
        var settings = _settings.Current;
        var sopUids = new HashSet<string>();

        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Image);
        request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyInstanceUid);
        request.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, "");
        request.Dataset.AddOrUpdate(DicomTag.SOPInstanceUID, "");

        var final = new FinalStatus();
        request.OnResponseReceived += (req, response) =>
        {
            if (response.Status != DicomStatus.Pending)
                final.Value = response.Status;

            if (response.Status != DicomStatus.Pending || response.Dataset is null)
                return;
            var sop = response.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "");
            if (!string.IsNullOrWhiteSpace(sop))
                sopUids.Add(sop);
        };

        var client = CreateClient(node, settings);
        await client.AddRequestAsync(request);
        await client.SendAsync(ct);
        EnsureFindSucceeded(final, node, "Instance-count C-FIND");
        return sopUids.Count;
    }

    private static int? GetInt(DicomDataset ds, DicomTag tag)
    {
        var raw = ds.GetSingleValueOrDefault(tag, "");
        return int.TryParse(raw, out var n) ? n : null;
    }

    private static string? JoinModalities(DicomDataset ds)
    {
        if (!ds.Contains(DicomTag.ModalitiesInStudy))
            return null;
        var values = ds.GetValues<string>(DicomTag.ModalitiesInStudy);
        return values is { Length: > 0 } ? string.Join('\\', values) : null;
    }
}
