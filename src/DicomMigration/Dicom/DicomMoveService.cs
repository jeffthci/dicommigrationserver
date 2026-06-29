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

    public async Task<MoveResult> MoveStudyAsync(string studyInstanceUid, CancellationToken ct)
    {
        var settings = _settings.Current;
        var destinationAe = settings.Destination.AeTitle;
        int completed = 0, failed = 0, warnings = 0;
        DicomStatus? finalStatus = null;
        string? failureComment = null;

        var request = new DicomCMoveRequest(destinationAe, studyInstanceUid);

        request.OnResponseReceived += (req, response) =>
        {
            // Pending responses carry running tallies; the final response carries the totals.
            completed = response.Completed;
            failed = response.Failures;
            warnings = response.Warnings;

            if (response.Status.State != DicomState.Pending)
            {
                finalStatus = response.Status;
                if (response.Status.State != DicomState.Success)
                    failureComment = response.Status.Description;
            }

            _logger.LogDebug(
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
