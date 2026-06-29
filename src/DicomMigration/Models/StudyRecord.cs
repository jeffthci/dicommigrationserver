namespace DicomMigration.Models;

/// <summary>
/// One row in the <c>studies</c> table: an inventoried study and its migration state.
/// Column names use snake_case; Dapper maps them via MatchNamesWithUnderscores.
/// </summary>
public sealed class StudyRecord
{
    public string StudyInstanceUid { get; set; } = "";
    public string? PatientId { get; set; }
    public string? PatientName { get; set; }
    public string? StudyDate { get; set; }
    public string? StudyDescription { get; set; }
    public string? AccessionNumber { get; set; }
    public string? Modalities { get; set; }

    public int? SourceSeriesCount { get; set; }
    public int? SourceInstanceCount { get; set; }

    public string Status { get; set; } = MigrationStatus.Inventoried;

    public int? MoveCompleted { get; set; }
    public int? MoveFailedCount { get; set; }
    public int? MoveWarnings { get; set; }

    public int? DestSeriesCount { get; set; }
    public int? DestInstanceCount { get; set; }

    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public string? InventoriedAt { get; set; }
    public string? MovedAt { get; set; }
    public string? VerifiedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

/// <summary>The lifecycle states a study moves through. Stored as text in SQLite.</summary>
public static class MigrationStatus
{
    public const string Inventoried = "inventoried";
    public const string MovePending = "move_pending";
    public const string Moved = "moved";
    public const string MoveFailed = "move_failed";
    public const string Verified = "verified";
    public const string Mismatch = "mismatch";
    public const string VerifyFailed = "verify_failed";

    public static readonly string[] All =
    {
        Inventoried, MovePending, Moved, MoveFailed, Verified, Mismatch, VerifyFailed
    };
}
