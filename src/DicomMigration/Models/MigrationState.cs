namespace DicomMigration.Models;

/// <summary>
/// Live progress of whatever phase the orchestrator is currently running.
/// Exposed via /api/status and polled by the dashboard.
/// </summary>
public sealed class MigrationState
{
    public string Phase { get; set; } = "idle";
    public bool IsRunning { get; set; }
    public int Processed { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public string? CurrentItem { get; set; }
    public string? Message { get; set; }
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }

    public MigrationState Clone() => (MigrationState)MemberwiseClone();
}

/// <summary>Filter applied to the inventory C-FIND query.</summary>
public sealed class InventoryFilter
{
    public string? PatientId { get; set; }
    public string? StudyDateRange { get; set; } // DICOM range, e.g. 20200101-20201231
    public string? AccessionNumber { get; set; }
    public string? ModalitiesInStudy { get; set; }
}
