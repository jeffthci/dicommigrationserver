namespace DicomMigration.Models;

/// <summary>
/// One DICOM operation currently in flight. The orchestrator can run several C-MOVEs at once
/// (Move.MaxParallelMoves), so this is a list rather than a single "current item": a lone string
/// would be overwritten by whichever parallel task wrote last and show an arbitrary member of the
/// working set.
///
/// Immutable so the state snapshot handed to the API cannot tear while a mover updates its slot.
/// </summary>
public sealed record ActiveOperation(
    string StudyInstanceUid,
    string? Label,          // "PEREZ^ESTEBAN - C Spine Complete", for humans
    string Operation,       // "C-MOVE", "C-FIND STUDY", "C-FIND IMAGE", "C-ECHO"
    string PeerAe,
    string PeerHost,
    int PeerPort,
    string StartedAtUtc,
    int SubOpsCompleted,    // instances the source reports transferred so far
    int SubOpsRemaining,    // -1 when the peer does not report it
    int SubOpsFailed,
    string? LastProgressAtUtc);

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

    /// <summary>Everything happening right now, one entry per parallel slot.</summary>
    public List<ActiveOperation> InFlight { get; set; } = new();

    /// <summary>Inventory chunking: which date window is being queried, and how many there are.</summary>
    public int ChunkIndex { get; set; }
    public int ChunkCount { get; set; }
    public string? ChunkRange { get; set; }

    /// <summary>
    /// Total instances the source has reported transferred during this phase. Lets the dashboard
    /// show that a single large study is still moving even while the study counter sits still.
    /// </summary>
    public long InstancesTransferred { get; set; }

    /// <summary>
    /// Last time anything at all advanced. A hung association looks exactly like a slow one --
    /// the counters simply stop -- so the dashboard uses this to distinguish "working" from
    /// "stalled" rather than guessing from elapsed time.
    /// </summary>
    public string? LastProgressAtUtc { get; set; }

    /// <summary>
    /// Bumped on every mutation. The dashboard skips re-rendering when it has not changed, which
    /// is what makes a sub-second poll cheap.
    /// </summary>
    public long Rev { get; set; }

    /// <summary>
    /// Explicit copy. MemberwiseClone would share the InFlight list instance with the live state,
    /// so the API could serialize it while a mover was mutating it. The entries themselves are
    /// immutable records, so copying the list is enough.
    /// </summary>
    public MigrationState Clone()
    {
        var c = (MigrationState)MemberwiseClone();
        c.InFlight = new List<ActiveOperation>(InFlight);
        return c;
    }
}

/// <summary>Filter applied to the inventory C-FIND query.</summary>
public sealed class InventoryFilter
{
    public string? PatientId { get; set; }
    public string? StudyDateRange { get; set; } // DICOM range, e.g. 20200101-20201231
    public string? AccessionNumber { get; set; }
    public string? ModalitiesInStudy { get; set; }
}
