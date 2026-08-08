using DicomMigration.Services;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;

namespace DicomMigration.Dicom;

/// <summary>Outcome of a single study C-MOVE, parsed from the final response sub-operation counts.</summary>
public sealed record MoveResult(
    bool Success,
    int Completed,
    int Failed,
    int Warnings,
    string? Error)
{
    public static MoveResult Fail(string error) => new(false, 0, 0, 0, error);
}

/// <summary>
/// Issues C-MOVE-RQ to the source PACS, naming the destination AE Title. The source performs
/// the actual C-STORE transfer; we only orchestrate and read back the sub-operation tallies.
/// The destination AE must be registered (AE -> host/port) on the source PACS for this to work.
/// </summary>
public sealed class DicomMoveService
{
    private readonly IDicomClientFactory _clientFactory;
    private readonly SettingsStore _settings;
    private readonly ILogger<DicomMoveService> _logger;

    public DicomMoveService(
        IDicomClientFactory clientFactory,
        SettingsStore settings,
        ILogger<DicomMoveService> logger)
    {
        _clientFactory = clientFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <param name="onProgress">
    /// (completed, remaining, failed) sub-operation counts, raised as the source reports progress
    /// so the dashboard can show a large study advancing instance-by-instance instead of appearing
    /// frozen for minutes. These numbers already arrived on every pending response and were
    /// previously discarded into a Debug log that the shipped configuration suppresses.
    /// Raised at most once a second per study, plus once on the final response.
    /// </param>
    public async Task<MoveResult> MoveStudyAsync(
        string studyInstanceUid,
        CancellationToken ct,
        Action<int, int, int>? onProgress = null)
    {
        var settings = _settings.Current;
        var destinationAe = settings.Destination.AeTitle;
        int completed = 0, failed = 0, warnings = 0;
        DicomStatus? finalStatus = null;
        string? failureComment = null;

        var request = new DicomCMoveRequest(destinationAe, studyInstanceUid);

        // A busy SCP can emit a pending response per instance. Coalescing keeps a chatty peer from
        // spinning the orchestrator's state lock on a large study.
        var lastRaised = DateTime.MinValue;

        request.OnResponseReceived += (req, response) =>
        {
            // Pending responses carry running tallies; the final response carries the totals.
            completed = response.Completed;
            failed = response.Failures;
            warnings = response.Warnings;

            var isFinal = response.Status.State != DicomState.Pending;
            if (isFinal)
            {
                finalStatus = response.Status;
                if (response.Status.State != DicomState.Success)
                    failureComment = response.Status.Description;
            }

            if (onProgress is not null)
            {
                var now = DateTime.UtcNow;
                if (isFinal || now - lastRaised >= TimeSpan.FromSeconds(1))
                {
                    lastRaised = now;
                    onProgress(completed, response.Remaining, failed);
                }
            }

            // Information, not Debug: these are the decision points for diagnosing a slow or
            // partial transfer, and the shipped Serilog configuration suppresses Debug.
            _logger.LogInformation(
                "C-MOVE {Uid}: status={Status} remaining={Remaining} completed={Completed} failed={Failed} warnings={Warnings}",
                studyInstanceUid, response.Status, response.Remaining, completed, failed, warnings);
        };

        var source = settings.Source;
        var client = _clientFactory.Create(source.Host, source.Port, source.UseTls, settings.LocalAeTitle, source.AeTitle);
        client.ServiceOptions.RequestTimeout = TimeSpan.FromMinutes(settings.Move.MoveTimeoutMinutes);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(settings.Move.MoveTimeoutMinutes));

        try
        {
            await client.AddRequestAsync(request);
            await client.SendAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return MoveResult.Fail($"C-MOVE timed out after {settings.Move.MoveTimeoutMinutes} min");
        }
        catch (OperationCanceledException)
        {
            // The operator cancelled. Let this propagate instead of falling into the generic
            // handler below, which would record a permanent move_failed with an empty DIMSE
            // status -- a study that was transferring fine would be indistinguishable from one
            // the source rejected. Measured during the parallelism test: every cancelled run
            // produced exactly one bogus "failure" per in-flight slot.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "C-MOVE association failed for {Uid}", studyInstanceUid);
            return MoveResult.Fail(ex.Message);
        }

        var success = finalStatus?.State == DicomState.Success && failed == 0;
        var error = success
            ? null
            : failureComment ?? $"status={finalStatus}, failed sub-operations={failed}";

        return new MoveResult(success, completed, failed, warnings, error);
    }
}
