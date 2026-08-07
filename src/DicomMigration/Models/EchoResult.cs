namespace DicomMigration.Models;

/// <summary>
/// Outcome of a C-ECHO connectivity test against a remote AE. A failure is a normal,
/// expected result here (that is what the test is for), so this carries the error text
/// rather than letting the exception escape as a 500.
/// </summary>
/// <param name="Ok">True only if the remote returned a success status.</param>
/// <param name="Message">Human-readable outcome, shown verbatim in the dashboard.</param>
/// <param name="ElapsedMs">Round-trip time including association negotiation.</param>
public sealed record EchoResult(bool Ok, string Message, long ElapsedMs)
{
    public static EchoResult Success(long ms) => new(true, "Success", ms);
    public static EchoResult Failure(string message, long ms) => new(false, message, ms);
}

/// <summary>
/// Body for POST /api/test-connection. <paramref name="Target"/> selects which saved node to
/// test; the remaining fields optionally override it so the dashboard can test values typed
/// into the Connections form before they are saved.
/// </summary>
public sealed record TestConnectionRequest(string? Target, string? AeTitle, string? Host, int? Port);
