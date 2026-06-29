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

    private IDicomClient CreateClient(DicomNode node, DicomSettings settings)
    {
        var client = _clientFactory.Create(node.Host, node.Port, node.UseTls, settings.LocalAeTitle, node.AeTitle);
        client.ServiceOptions.RequestTimeout = TimeSpan.FromSeconds(settings.Move.AssociationTimeoutSeconds);
        client.NegotiateAsyncOps();
        return client;
    }

    /// <summary>
    /// STUDY-level C-FIND against the source. Returns one record per study with patient
    /// metadata and source-side series/instance counts.
    /// </summary>
    public async Task<IReadOnlyList<StudyRecord>> InventorySourceAsync(InventoryFilter filter, CancellationToken ct)
    {
        var settings = _settings.Current;
        var results = new List<StudyRecord>();

        var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);
        var ds = request.Dataset;
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, "");
        ds.AddOrUpdate(DicomTag.PatientID, filter.PatientId ?? "");
        ds.AddOrUpdate(DicomTag.PatientName, "");
        ds.AddOrUpdate(DicomTag.StudyDate, filter.StudyDateRange ?? "");
        ds.AddOrUpdate(DicomTag.StudyTime, "");
        ds.AddOrUpdate(DicomTag.StudyDescription, "");
        ds.AddOrUpdate(DicomTag.AccessionNumber, filter.AccessionNumber ?? "");
        ds.AddOrUpdate(DicomTag.ModalitiesInStudy, filter.ModalitiesInStudy ?? "");
        ds.AddOrUpdate(DicomTag.NumberOfStudyRelatedSeries, "");
        ds.AddOrUpdate(DicomTag.NumberOfStudyRelatedInstances, "");

        request.OnResponseReceived += (req, response) =>
        {
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

        request.OnResponseReceived += (req, response) =>
        {
            if (response.Status != DicomStatus.Pending || response.Dataset is null)
                return;
            found = true;
            series = GetInt(response.Dataset, DicomTag.NumberOfStudyRelatedSeries);
            instances = GetInt(response.Dataset, DicomTag.NumberOfStudyRelatedInstances);
        };

        var client = CreateClient(node, settings);
        await client.AddRequestAsync(request);
        await client.SendAsync(ct);
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

        request.OnResponseReceived += (req, response) =>
        {
            if (response.Status != DicomStatus.Pending || response.Dataset is null)
                return;
            var sop = response.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "");
            if (!string.IsNullOrWhiteSpace(sop))
                sopUids.Add(sop);
        };

        var client = CreateClient(node, settings);
        await client.AddRequestAsync(request);
        await client.SendAsync(ct);
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
