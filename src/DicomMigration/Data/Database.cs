using Dapper;
using Microsoft.Data.Sqlite;

namespace DicomMigration.Data;

/// <summary>
/// Owns the SQLite connection string and one-time schema creation.
/// SQLite is single-file; WAL mode lets the web reads run alongside the worker writes.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    /// <summary>Absolute path to the SQLite file, so it can be backed up before a reset.</summary>
    public string FilePath { get; }

    public Database(IConfiguration config, ILogger<Database> logger)
    {
        var path = config["Database:Path"] ?? "data/migration.db";
        // Resolve a relative path against the app base directory (not the process CWD) so the
        // init-db command and the web/service host always agree on the same file. An absolute
        // Database:Path is honored as-is.
        var fullPath = Path.GetFullPath(path, AppContext.BaseDirectory);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        FilePath = fullPath;

        // Dapper maps source_instance_count -> SourceInstanceCount, etc.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        logger.LogInformation("SQLite database at {Path}", fullPath);
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        return conn;
    }

    /// <summary>
    /// Copies the database to a timestamped file in the archive directory and returns its path.
    /// Uses SQLite VACUUM INTO rather than a file copy: the database is in WAL mode and open, so
    /// copying the .db file alone could miss committed pages still sitting in the -wal file.
    /// VACUUM INTO produces a consistent, self-contained snapshot of the live database.
    /// </summary>
    public string BackupTo(string archiveDir)
    {
        Directory.CreateDirectory(archiveDir);

        // The snapshot is a full copy of the inventory: patient names, IDs, accession numbers.
        // Directory.CreateDirectory uses the default umask, which left it world-readable, so
        // restrict both the directory and the file to the service account.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(archiveDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var target = Path.Combine(archiveDir, "migration-" + stamp + ".db");

        using (var conn = Open())
        {
            // VACUUM INTO does not accept a parameter; the path is server-generated, never user input.
            conn.Execute("VACUUM INTO '" + target.Replace("'", "''") + "';");
        }

        if (!OperatingSystem.IsWindows() && File.Exists(target))
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        return target;
    }

    public void Initialize()
    {
        using var conn = Open();
        conn.Execute(Schema);
        AddMissingColumns(conn);
    }

    /// <summary>
    /// Adds columns introduced after a database was first created. CREATE TABLE IF NOT EXISTS is a
    /// no-op on an existing table, so new columns would silently never appear on an upgraded
    /// install -- and the code reading them would fail at runtime rather than at deploy.
    /// SQLite has no "ADD COLUMN IF NOT EXISTS", so check the table info first.
    /// </summary>
    private static void AddMissingColumns(SqliteConnection conn)
    {
        var existing = conn.Query<string>("SELECT name FROM pragma_table_info('studies');")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Wall-clock milliseconds the C-MOVE took. Persisted so throughput and the completion
        // estimate survive a restart instead of being re-learned from scratch each run.
        if (!existing.Contains("move_ms"))
            conn.Execute("ALTER TABLE studies ADD COLUMN move_ms INTEGER;");
    }

    private const string Schema = @"
CREATE TABLE IF NOT EXISTS studies (
    study_instance_uid     TEXT PRIMARY KEY,
    patient_id             TEXT,
    patient_name           TEXT,
    study_date             TEXT,
    study_description      TEXT,
    accession_number       TEXT,
    modalities             TEXT,
    source_series_count    INTEGER,
    source_instance_count  INTEGER,
    status                 TEXT NOT NULL DEFAULT 'inventoried',
    move_completed         INTEGER,
    move_failed_count      INTEGER,
    move_warnings          INTEGER,
    dest_series_count      INTEGER,
    dest_instance_count    INTEGER,
    attempts               INTEGER NOT NULL DEFAULT 0,
    last_error             TEXT,
    inventoried_at         TEXT,
    moved_at               TEXT,
    verified_at            TEXT,
    updated_at             TEXT
);

CREATE INDEX IF NOT EXISTS idx_studies_status ON studies(status);

CREATE TABLE IF NOT EXISTS event_log (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    study_instance_uid  TEXT,
    level               TEXT NOT NULL,
    message             TEXT NOT NULL,
    created_at          TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_event_log_created ON event_log(created_at);

CREATE TABLE IF NOT EXISTS settings (
    key         TEXT PRIMARY KEY,
    value       TEXT,
    updated_at  TEXT
);
";
}
