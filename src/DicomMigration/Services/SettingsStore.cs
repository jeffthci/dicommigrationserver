using Dapper;
using DicomMigration.Configuration;
using DicomMigration.Data;
using Microsoft.Extensions.Options;

namespace DicomMigration.Services;

/// <summary>
/// Runtime source of truth for operational configuration, persisted in the SQLite <c>settings</c>
/// table. Defaults come from appsettings.json (bootstrap); the installer seeds the table; the
/// dashboard can update throttle/verify values live. The orchestrator reads <see cref="Current"/>
/// at the point of use, so changes take effect without a restart.
/// </summary>
public sealed class SettingsStore
{
    private readonly Database _db;
    private readonly DicomSettings _defaults;
    private readonly ILogger<SettingsStore> _logger;
    private readonly object _lock = new();
    private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Upper bound on concurrent C-MOVE associations against the source PACS. Public because the
    /// orchestrator also clamps on read: the installers write the settings table directly and so
    /// bypass this validation, and a stored out-of-range value must not brick the dashboard's
    /// Save button (which re-posts every field) or uncork unbounded parallelism.
    /// </summary>
    public const int MaxParallelMovesCeiling = 16;

    // Flat keys mirror the appsettings "Dicom" structure (Section omitted).
    private static readonly HashSet<string> IntKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Source.Port", "Destination.Port",
        "Move.MaxParallelMoves", "Move.AssociationTimeoutSeconds",
        "Move.MoveTimeoutMinutes", "Move.MaxAttempts", "Move.DelayBetweenMovesMs",
        "Inventory.ChunkDays", "Inventory.DelayBetweenChunksMs"
    };
    private static readonly HashSet<string> BoolKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Source.UseTls", "Destination.UseTls", "Inventory.FetchSeriesCounts"
    };
    private static readonly HashSet<string> PortKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Source.Port", "Destination.Port"
    };
    private static readonly HashSet<string> AeTitleKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "LocalAeTitle", "Source.AeTitle", "Destination.AeTitle"
    };
    private static readonly HashSet<string> HostKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Source.Host", "Destination.Host"
    };

    public SettingsStore(Database db, IOptions<DicomSettings> defaults, ILogger<SettingsStore> logger)
    {
        _db = db;
        _defaults = defaults.Value;
        _logger = logger;
    }

    private Dictionary<string, string> Flatten(DicomSettings s) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["LocalAeTitle"] = s.LocalAeTitle,
        ["Source.AeTitle"] = s.Source.AeTitle,
        ["Source.Host"] = s.Source.Host,
        ["Source.Port"] = s.Source.Port.ToString(),
        ["Source.UseTls"] = s.Source.UseTls.ToString(),
        ["Destination.AeTitle"] = s.Destination.AeTitle,
        ["Destination.Host"] = s.Destination.Host,
        ["Destination.Port"] = s.Destination.Port.ToString(),
        ["Destination.UseTls"] = s.Destination.UseTls.ToString(),
        ["Move.MaxParallelMoves"] = s.Move.MaxParallelMoves.ToString(),
        ["Move.AssociationTimeoutSeconds"] = s.Move.AssociationTimeoutSeconds.ToString(),
        ["Move.MoveTimeoutMinutes"] = s.Move.MoveTimeoutMinutes.ToString(),
        ["Move.MaxAttempts"] = s.Move.MaxAttempts.ToString(),
        ["Move.DelayBetweenMovesMs"] = s.Move.DelayBetweenMovesMs.ToString(),
        ["Verify.Level"] = s.Verify.Level,
        ["Inventory.FetchSeriesCounts"] = s.Inventory.FetchSeriesCounts.ToString(),
        ["Inventory.ChunkDays"] = s.Inventory.ChunkDays.ToString(),
        ["Inventory.DelayBetweenChunksMs"] = s.Inventory.DelayBetweenChunksMs.ToString()
    };

    /// <summary>The set of keys that may be written through the API / installer.</summary>
    public IReadOnlyCollection<string> EditableKeys => Flatten(_defaults).Keys;

    /// <summary>
    /// Seed the settings table from appsettings defaults and load it into memory.
    /// With <paramref name="overwrite"/> = true (installer), all keys are (re)written from config;
    /// otherwise only missing keys are filled, preserving any values an admin already changed.
    /// </summary>
    public void Initialize(bool overwrite = false)
    {
        using var conn = _db.Open();
        var existing = LoadRows(conn);
        var defaults = Flatten(_defaults);
        var now = DateTime.UtcNow.ToString("o");

        foreach (var (key, value) in defaults)
        {
            if (overwrite || !existing.ContainsKey(key))
            {
                conn.Execute(
                    "INSERT INTO settings (key, value, updated_at) VALUES (@key, @value, @now) " +
                    "ON CONFLICT(key) DO UPDATE SET value = @value, updated_at = @now",
                    new { key, value, now });
            }
        }

        var loaded = LoadRows(conn);
        lock (_lock) _cache = loaded;
        _logger.LogInformation("Settings loaded ({Count} keys, overwrite={Overwrite})", loaded.Count, overwrite);
    }

    public void Reload()
    {
        using var conn = _db.Open();
        var rows = LoadRows(conn);
        lock (_lock) _cache = rows;
    }

    private static Dictionary<string, string> LoadRows(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in conn.Query("SELECT key, value FROM settings"))
        {
            var key = (string?)row.key;
            if (key is not null) dict[key] = (string?)row.value ?? "";
        }
        return dict;
    }

    private string GetString(string key, string fallback)
    {
        lock (_lock) return _cache.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;
    }
    private int GetInt(string key, int fallback) => int.TryParse(GetString(key, ""), out var n) ? n : fallback;
    private bool GetBool(string key, bool fallback) => bool.TryParse(GetString(key, ""), out var b) ? b : fallback;

    /// <summary>A fully-materialized settings snapshot built from the live DB cache + defaults.</summary>
    public DicomSettings Current
    {
        get
        {
            var d = _defaults;
            return new DicomSettings
            {
                LocalAeTitle = GetString("LocalAeTitle", d.LocalAeTitle),
                Source = new DicomNode
                {
                    AeTitle = GetString("Source.AeTitle", d.Source.AeTitle),
                    Host = GetString("Source.Host", d.Source.Host),
                    Port = GetInt("Source.Port", d.Source.Port),
                    UseTls = GetBool("Source.UseTls", d.Source.UseTls)
                },
                Destination = new DicomNode
                {
                    AeTitle = GetString("Destination.AeTitle", d.Destination.AeTitle),
                    Host = GetString("Destination.Host", d.Destination.Host),
                    Port = GetInt("Destination.Port", d.Destination.Port),
                    UseTls = GetBool("Destination.UseTls", d.Destination.UseTls)
                },
                Move = new MoveSettings
                {
                    MaxParallelMoves = Math.Max(1, GetInt("Move.MaxParallelMoves", d.Move.MaxParallelMoves)),
                    AssociationTimeoutSeconds = GetInt("Move.AssociationTimeoutSeconds", d.Move.AssociationTimeoutSeconds),
                    MoveTimeoutMinutes = GetInt("Move.MoveTimeoutMinutes", d.Move.MoveTimeoutMinutes),
                    MaxAttempts = GetInt("Move.MaxAttempts", d.Move.MaxAttempts),
                    DelayBetweenMovesMs = Math.Max(0, GetInt("Move.DelayBetweenMovesMs", d.Move.DelayBetweenMovesMs))
                },
                Verify = new VerifySettings { Level = GetString("Verify.Level", d.Verify.Level) },
                Inventory = new InventorySettings
                {
                    FetchSeriesCounts = GetBool("Inventory.FetchSeriesCounts", d.Inventory.FetchSeriesCounts),
                    ChunkDays = GetInt("Inventory.ChunkDays", d.Inventory.ChunkDays),
                    DelayBetweenChunksMs = GetInt("Inventory.DelayBetweenChunksMs", d.Inventory.DelayBetweenChunksMs)
                }
            };
        }
    }

    /// <summary>
    /// Apply a set of flat key/value changes. Unknown keys are ignored; numeric/bool keys are
    /// validated so a bad value can't poison the cache. Persists to the DB and refreshes memory.
    /// </summary>
    public async Task UpdateAsync(IDictionary<string, string> changes)
    {
        var editable = new HashSet<string>(EditableKeys, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow.ToString("o");

        // Validate EVERY change before writing ANY of them. Previously validation happened inside
        // the write loop, so a request whose third key was invalid left the first two committed
        // while the in-memory cache kept the old values -- the DB and the running config diverged,
        // and the divergence only surfaced on the next restart.
        var pending = new List<(string Key, string Value)>();
        foreach (var (key, rawValue) in changes)
        {
            if (!editable.Contains(key)) continue;
            var value = rawValue?.Trim() ?? "";
            Validate(key, value);
            pending.Add((key, value));
        }

        if (pending.Count == 0) return;

        using var conn = _db.Open();
        using (var tx = conn.BeginTransaction())
        {
            foreach (var (key, value) in pending)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO settings (key, value, updated_at) VALUES (@key, @value, @now) " +
                    "ON CONFLICT(key) DO UPDATE SET value = @value, updated_at = @now",
                    new { key, value, now }, tx);
            }
            tx.Commit();
        }

        var loaded = LoadRows(conn);
        lock (_lock) _cache = loaded;
    }

    private static void Validate(string key, string value)
    {
        if (IntKeys.Contains(key))
        {
            if (!int.TryParse(value, out var n))
                throw new ArgumentException($"Setting '{key}' must be an integer (got '{value}').");
            if (PortKeys.Contains(key) && (n < 1 || n > 65535))
                throw new ArgumentException($"Setting '{key}' must be a port between 1 and 65535 (got {n}).");

            // Parallel C-MOVE associations land on a live clinical PACS, many of which allow only
            // one. An unbounded value here is a denial of service against imaging that is in use
            // for patient care, so cap it rather than trusting the caller.
            if (key.Equals("Move.MaxParallelMoves", StringComparison.OrdinalIgnoreCase) && (n < 1 || n > MaxParallelMovesCeiling))
                throw new ArgumentException($"Setting '{key}' must be between 1 and {MaxParallelMovesCeiling} (got {n}).");

            if (!PortKeys.Contains(key) && n < 0)
                throw new ArgumentException($"Setting '{key}' may not be negative (got {n}).");
        }
        else if (BoolKeys.Contains(key))
        {
            if (!bool.TryParse(value, out _))
                throw new ArgumentException($"Setting '{key}' must be true/false (got '{value}').");
        }
        else if (AeTitleKeys.Contains(key))
        {
            if (value.Length == 0)
                throw new ArgumentException($"Setting '{key}' (AE Title) is required.");
            if (value.Length > 16)
                throw new ArgumentException($"Setting '{key}' (AE Title) must be 16 characters or fewer.");
            if (value.Contains('\\'))
                throw new ArgumentException($"Setting '{key}' (AE Title) may not contain a backslash.");
        }
        else if (HostKeys.Contains(key))
        {
            if (value.Length == 0)
                throw new ArgumentException($"Setting '{key}' (host) is required.");
        }
        else if (key.Equals("Verify.Level", StringComparison.OrdinalIgnoreCase))
        {
            // ParseLevel falls back to Study for anything unrecognized, so an unvalidated value
            // silently downgraded verification depth instead of reporting the mistake.
            if (!Enum.TryParse<VerifyLevel>(value, ignoreCase: true, out _))
                throw new ArgumentException($"Setting '{key}' must be Study, Series or Instance (got '{value}').");
        }
    }
}
