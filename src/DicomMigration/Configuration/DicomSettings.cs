namespace DicomMigration.Configuration;

/// <summary>
/// Root DICOM configuration bound from the "Dicom" section of appsettings.json.
/// </summary>
public sealed class DicomSettings
{
    public const string SectionName = "Dicom";

    /// <summary>Calling AE Title this migration server presents on every association.</summary>
    public string LocalAeTitle { get; set; } = "MIGRATE";

    /// <summary>Source PACS we inventory and instruct to move studies.</summary>
    public DicomNode Source { get; set; } = new();

    /// <summary>Destination PACS we verify against. The source pushes here via C-STORE.</summary>
    public DicomNode Destination { get; set; } = new();

    public MoveSettings Move { get; set; } = new();
    public VerifySettings Verify { get; set; } = new();
    public InventorySettings Inventory { get; set; } = new();
}

/// <summary>A remote DICOM application entity (AE Title + network endpoint).</summary>
public sealed class DicomNode
{
    public string AeTitle { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 104;
    public bool UseTls { get; set; }
}

public sealed class MoveSettings
{
    /// <summary>How many C-MOVE associations to run concurrently. Many PACS allow only 1.</summary>
    public int MaxParallelMoves { get; set; } = 1;

    /// <summary>Timeout for establishing the association before a C-MOVE.</summary>
    public int AssociationTimeoutSeconds { get; set; } = 60;

    /// <summary>Hard ceiling for a single study's C-MOVE to complete.</summary>
    public int MoveTimeoutMinutes { get; set; } = 120;

    /// <summary>How many times to retry a failed move before giving up.</summary>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>Optional pacing delay between studies to avoid overwhelming the source.</summary>
    public int DelayBetweenMovesMs { get; set; }

    /// <summary>
    /// Accept a C-MOVE that finished with failed sub-operations, PROVIDED every counted image
    /// instance was delivered (Completed >= NumberOfStudyRelatedInstances).
    ///
    /// This exists for a known condition on the source archive: some Presentation State objects
    /// were lost in a server data-loss incident, so the source cannot send them. The study still
    /// contains a PR series in its index, so the C-MOVE reports one failed sub-operation and the
    /// whole study is marked move_failed even though every diagnostic image arrived intact.
    ///
    /// The guard is the point: it accepts only what the source's own instance count says is
    /// complete. A move that actually lost imaging has Completed &lt; instance count and still
    /// fails. Accepted studies keep their failed sub-operation count and carry an explanatory
    /// last_error, so this never silently hides a shortfall.
    /// </summary>
    public bool AcceptMissingNonImageObjects { get; set; }
}

public sealed class VerifySettings
{
    /// <summary>Study | Series | Instance — how deep to compare source vs destination.</summary>
    public string Level { get; set; } = "Study";
}

public sealed class InventorySettings
{
    /// <summary>If true, follow each study with a SERIES-level C-FIND to record series counts.</summary>
    public bool FetchSeriesCounts { get; set; }

    /// <summary>
    /// Split the inventory into date windows of this many days, issuing one C-FIND per window
    /// instead of a single query for the whole range. 0 disables chunking (one query).
    /// Chunking keeps each association short and each result set small, which is gentler on a
    /// source PACS than one open-ended query and lets a large archive be walked in steps.
    /// Requires a study date range; without one there is nothing to split.
    /// </summary>
    public int ChunkDays { get; set; }

    /// <summary>Pause between inventory chunks, to pace load on the source.</summary>
    public int DelayBetweenChunksMs { get; set; }
}

public enum VerifyLevel
{
    Study,
    Series,
    Instance
}
