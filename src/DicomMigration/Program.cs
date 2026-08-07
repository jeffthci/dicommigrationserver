using DicomMigration.Configuration;
using DicomMigration.Data;
using DicomMigration.Dicom;
using DicomMigration.Models;
using DicomMigration.Services;
using FellowOakDicom;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

// One-shot installer command: create the SQLite schema and seed the settings table from config,
// then exit. Invoked by install.ps1 so the database is preconfigured with no user intervention.
if (args.Length > 0 && string.Equals(args[0], "init-db", StringComparison.OrdinalIgnoreCase))
{
    await RunInitDbAsync(args);
    return;
}

// Service hosts do not start in the install directory: a Windows service gets
// CWD = C:\Windows\System32, and a systemd unit gets whatever WorkingDirectory= says (/ if unset).
// Anchor relative paths (Serilog's logs/) to the install directory instead. Database:Path is
// already resolved against AppContext.BaseDirectory, so it does not depend on this.
if (WindowsServiceHelpers.IsWindowsService() || SystemdHelpers.IsSystemdService())
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);

// Both are no-ops on the other platform (and when run interactively), so the same build serves
// the Windows MSI/service install and the Linux systemd install.
// UseSystemd() signals readiness via sd_notify, so the unit must declare Type=notify.
builder.Host.UseWindowsService(o => o.ServiceName = "DICOM Migration Server");
builder.Host.UseSystemd();

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/migration-.log", rollingInterval: RollingInterval.Day));

// fo-dicom DI: registers IDicomClientFactory and friends.
builder.Services.AddFellowOakDicom();

builder.Services.Configure<DicomSettings>(builder.Configuration.GetSection(DicomSettings.SectionName));

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<StudyRepository>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<DicomQueryService>();
builder.Services.AddSingleton<DicomMoveService>();
builder.Services.AddSingleton<MigrationOrchestrator>();

var app = builder.Build();

// Create schema (idempotent) and load settings from the DB (seed defaults if first run).
app.Services.GetRequiredService<Database>().Initialize();
app.Services.GetRequiredService<SettingsStore>().Initialize();

// Reclaim studies stranded in move_pending by a previous process that stopped mid-move (a crash,
// or an installer stopping the service to upgrade). Without this they are invisible to every
// phase and look accounted-for on the dashboard while never having reached the destination.
{
    var repo = app.Services.GetRequiredService<StudyRepository>();
    var reclaimed = await repo.ReclaimInterruptedMovesAsync();
    if (reclaimed > 0)
    {
        app.Logger.LogWarning("Reclaimed {Count} studies left in move_pending by an interrupted run; " +
                              "they are now move_failed and will be picked up by Retry.", reclaimed);
        await repo.LogEventAsync(null, "warn",
            $"Startup: reclaimed {reclaimed} studies interrupted mid-move (now move_failed)");
    }
}

// Tie the running phase to host shutdown. Cancelling alone is not enough: the phase body runs on
// a detached task, so without waiting the process would still exit mid-move and re-strand rows in
// move_pending (which startup reclaim then has to clean up). Block shutdown until the phase
// actually unwinds, bounded so a stuck association cannot hang the service stop indefinitely.
// The unit's TimeoutStopSec is 60s, so stay under it.
app.Lifetime.ApplicationStopping.Register(() =>
{
    var orch = app.Services.GetRequiredService<MigrationOrchestrator>();
    if (!orch.IsBusy) return;

    app.Logger.LogInformation("Shutdown requested while a phase is running; cancelling and waiting.");
    orch.Cancel();

    if (orch.WaitForIdle(TimeSpan.FromSeconds(30)))
        app.Logger.LogInformation("Phase stopped cleanly.");
    else
        app.Logger.LogWarning("Phase did not stop within 30s; exiting anyway. " +
                              "Any study left in move_pending will be reclaimed on next start.");
});

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// CSRF guard. The dashboard has no authentication, so a page on any other origin could otherwise
// POST here and start or cancel a migration against live PACS -- a plain form submission needs no
// CORS permission. Requiring a header that only same-origin script can set (browsers preflight
// custom headers, and this endpoint sends no CORS headers so the preflight fails) blocks that
// without adding a login. Non-browser callers (curl, scripts) simply send the header.
api.AddEndpointFilter(async (ctx, next) =>
{
    var req = ctx.HttpContext.Request;
    if (HttpMethods.IsPost(req.Method) && !req.Headers.ContainsKey("X-Requested-With"))
    {
        // Explain rather than returning a bare 403: a script calling this API needs to know the
        // header is the fix, not that it lacks credentials.
        return Results.Json(
            new { error = "Missing X-Requested-With header. Send 'X-Requested-With: XMLHttpRequest' on POST requests." },
            statusCode: StatusCodes.Status403Forbidden);
    }
    return await next(ctx);
});

api.MapGet("/status", async (MigrationOrchestrator orch, StudyRepository repo) => Results.Ok(new
{
    state = orch.State,
    busy = orch.IsBusy,
    counts = await repo.GetStatusCountsAsync()
}));

api.MapGet("/config", (SettingsStore store) =>
{
    var s = store.Current;
    return Results.Ok(new
    {
        local = s.LocalAeTitle,
        source = new { s.Source.AeTitle, s.Source.Host, s.Source.Port },
        destination = new { s.Destination.AeTitle, s.Destination.Host, s.Destination.Port },
        verifyLevel = s.Verify.Level,
        maxParallelMoves = s.Move.MaxParallelMoves,
        maxAttempts = s.Move.MaxAttempts
    });
});

// Full settings snapshot (structured) for the dashboard's throttle/config panel.
api.MapGet("/settings", (SettingsStore store) => Results.Ok(store.Current));

// Apply flat key/value changes (e.g. {"Move.DelayBetweenMovesMs":"500"}). Takes effect live.
api.MapPost("/settings", async (SettingsStore store, Dictionary<string, string> changes) =>
{
    try
    {
        await store.UpdateAsync(changes);
        return Results.Ok(store.Current);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// C-ECHO connectivity test for the dashboard's Connections panel. AE/host/port may be supplied
// in the body so the operator can test values typed into the form before saving them; anything
// omitted falls back to the saved settings for that target.
api.MapPost("/test-connection", async (
    DicomQueryService query,
    SettingsStore store,
    TestConnectionRequest req,
    CancellationToken ct) =>
{
    var settings = store.Current;

    var saved = req.Target?.ToLowerInvariant() switch
    {
        "source"      => settings.Source,
        "destination" => settings.Destination,
        _             => null
    };

    if (saved is null)
        return Results.BadRequest(new { error = "Target must be 'source' or 'destination'." });

    var host = string.IsNullOrWhiteSpace(req.Host) ? saved.Host : req.Host.Trim();
    var aeTitle = string.IsNullOrWhiteSpace(req.AeTitle) ? saved.AeTitle : req.AeTitle.Trim();
    var port = req.Port ?? saved.Port;

    if (string.IsNullOrWhiteSpace(aeTitle))
        return Results.BadRequest(new { error = "AE Title is required." });
    if (string.IsNullOrWhiteSpace(host))
        return Results.BadRequest(new { error = "Host is required." });
    if (port < 1 || port > 65535)
        return Results.BadRequest(new { error = $"Port must be between 1 and 65535 (got {port})." });

    var node = new DicomNode { AeTitle = aeTitle, Host = host, Port = port, UseTls = saved.UseTls };
    return Results.Ok(await query.EchoAsync(node, ct));
});

// limit/offset are clamped: SQLite treats a negative LIMIT as "no limit", so an unclamped
// caller could pull the entire PHI-bearing inventory in one response.
api.MapGet("/studies", async (StudyRepository repo, string? status, int? limit, int? offset) =>
    Results.Ok(await repo.GetPageAsync(status, Clamp(limit, 100, 1000), Math.Max(0, offset ?? 0))));

api.MapGet("/events", async (StudyRepository repo, int? limit) =>
    Results.Ok(await repo.GetRecentEventsAsync(Clamp(limit, 100, 1000))));

static int Clamp(int? requested, int fallback, int max) =>
    requested is null ? fallback : Math.Clamp(requested.Value, 1, max);

api.MapPost("/inventory", (MigrationOrchestrator orch, InventoryFilter? filter) =>
    StartPhase(() => orch.StartInventory(filter ?? new InventoryFilter())));

api.MapPost("/migrate", (MigrationOrchestrator orch) => StartPhase(orch.StartMigrate));

api.MapPost("/retry", (MigrationOrchestrator orch) => StartPhase(orch.StartRetry));

// Push a single study on demand. The UID is taken from the body rather than the route because
// DICOM UIDs are dot-separated and awkward to carry safely in a path segment.
api.MapPost("/move-study", (MigrationOrchestrator orch, MoveStudyRequest req) =>
    string.IsNullOrWhiteSpace(req.StudyInstanceUid)
        ? Results.BadRequest(new { error = "studyInstanceUid is required." })
        : StartPhase(() => orch.StartMoveStudy(req.StudyInstanceUid.Trim())));

// Re-check one study against the destination. Read-only (C-FIND), so it is the right tool when a
// destination that ingests asynchronously has left a study marked mismatch.
api.MapPost("/verify-study", (MigrationOrchestrator orch, MoveStudyRequest req) =>
    string.IsNullOrWhiteSpace(req.StudyInstanceUid)
        ? Results.BadRequest(new { error = "studyInstanceUid is required." })
        : StartPhase(() => orch.StartVerifyStudy(req.StudyInstanceUid.Trim())));

api.MapPost("/verify", (MigrationOrchestrator orch) => StartPhase(orch.StartVerify));

api.MapPost("/cancel", (MigrationOrchestrator orch) =>
{
    orch.Cancel();
    return Results.Ok(new { cancelled = true });
});

app.Run();

// Start a phase via the orchestrator's atomic, synchronous gate acquisition.
// Returns 202 if it started, or 409 if another phase is already running.
static IResult StartPhase(Func<bool> start) =>
    start()
        ? Results.Accepted(value: new { started = true })
        : Results.Conflict(new { error = "A migration phase is already running." });

// Build a minimal config + DI graph, create the database, and force-seed the settings table
// from configuration. Used by the installer; no web host is started.
static async Task RunInitDbAsync(string[] args)
{
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);

    var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? "Production";

    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole());

    var db = new Database(config, loggerFactory.CreateLogger<Database>());
    db.Initialize();

    var dicomSettings = new DicomSettings();
    config.GetSection(DicomSettings.SectionName).Bind(dicomSettings);

    var store = new SettingsStore(db, Options.Create(dicomSettings), loggerFactory.CreateLogger<SettingsStore>());
    store.Initialize(overwrite: true);

    Console.WriteLine("init-db: schema created and settings seeded from configuration.");
    await Task.CompletedTask;
}
