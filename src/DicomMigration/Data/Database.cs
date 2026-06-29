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

    public void Initialize()
    {
        using var conn = Open();
        conn.Execute(Schema);
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
