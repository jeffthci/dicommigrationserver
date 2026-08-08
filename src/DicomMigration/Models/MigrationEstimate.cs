namespace DicomMigration.Models;

/// <summary>
/// Raw measurements behind the completion estimate. Kept as measurements plus derived values
/// rather than a single "time remaining" string so the dashboard can show the operator what the
/// estimate is actually based on -- an ETA with no visible basis invites either false confidence
/// or dismissal.
/// </summary>
/// <param name="MovedStudies">Successful moves that recorded a duration.</param>
/// <param name="MovedInstances">Instances transferred across those moves.</param>
/// <param name="MovedMs">Total wall-clock time those moves took.</param>
/// <param name="PendingStudies">Studies still to move (inventoried + move_failed).</param>
/// <param name="PendingInstances">Instances those studies hold, per the source's counts.</param>
/// <param name="PendingUnknownInstances">
/// Pending studies whose instance count the source never reported. Their work is invisible to the
/// estimate, so it is surfaced rather than hidden -- a large number here means the ETA is a floor.
/// </param>
public sealed record MigrationEstimate(
    int MovedStudies,
    long MovedInstances,
    long MovedMs,
    int PendingStudies,
    long PendingInstances,
    int PendingUnknownInstances)
{
    /// <summary>Measured instances per second, or null until at least one timed move exists.</summary>
    public double? InstancesPerSecond =>
        MovedMs > 0 && MovedInstances > 0 ? MovedInstances / (MovedMs / 1000.0) : null;

    /// <summary>Mean instances per study among those already moved — used to price unknown-size work.</summary>
    public double? AverageInstancesPerStudy =>
        MovedStudies > 0 ? (double)MovedInstances / MovedStudies : null;

    /// <summary>
    /// Instances still to transfer, with studies of unknown size priced at the observed average
    /// rather than ignored. Without this the estimate silently under-counts.
    /// </summary>
    public long ProjectedPendingInstances =>
        PendingInstances + (long)Math.Round((AverageInstancesPerStudy ?? 0) * PendingUnknownInstances);

    /// <summary>Seconds of transfer remaining at the measured rate, or null if not yet measurable.</summary>
    public double? SecondsRemaining =>
        InstancesPerSecond is > 0 ? ProjectedPendingInstances / InstancesPerSecond.Value : null;

    /// <summary>
    /// How much to trust the rate. One study is an anecdote, especially since a single large study
    /// transfers faster per instance than many small ones (association setup dominates the small
    /// ones), so the sample size is reported rather than buried.
    /// </summary>
    public string Confidence =>
        MovedStudies >= 25 ? "measured" : MovedStudies >= 5 ? "provisional" : "rough";
}
