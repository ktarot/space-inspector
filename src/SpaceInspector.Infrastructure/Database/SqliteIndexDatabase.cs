using Microsoft.Data.Sqlite;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Infrastructure.Database;

public sealed partial class SqliteIndexDatabase : IIndexDatabase, IScanRepository
{
    private readonly string _path;
    private readonly string _connectionString;

    public SqliteIndexDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > 2) throw new InvalidOperationException("더 최신 버전에서 생성한 인덱스입니다. 앱을 업데이트하세요.");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Files (
                FileId INTEGER PRIMARY KEY,
                FullPath TEXT NOT NULL COLLATE NOCASE UNIQUE,
                FileName TEXT NOT NULL,
                Extension TEXT NOT NULL DEFAULT '',
                ParentDirectory TEXT NOT NULL,
                Drive TEXT NOT NULL,
                FileSize INTEGER CHECK (FileSize IS NULL OR FileSize >= 0),
                AllocatedSize INTEGER,
                CreationTimeUtc TEXT,
                ModifiedTimeUtc TEXT,
                Attributes INTEGER,
                FileCategory TEXT,
                FileType TEXT,
                PartialHash TEXT,
                FullHash TEXT,
                DuplicateGroupId TEXT,
                DuplicateStatus TEXT NOT NULL DEFAULT 'Unverified',
                DurationSeconds REAL,
                ScanTimeUtc TEXT,
                VolumeIdentifier TEXT,
                FileSystemIdentifier TEXT,
                Owner TEXT,
                AccessPermission TEXT,
                IsSystemProtected INTEGER,
                IsApplicationFile INTEGER,
                IsCloudPlaceholder INTEGER,
                OneDriveStatus TEXT NOT NULL DEFAULT 'Unknown',
                ScanStatus TEXT NOT NULL DEFAULT 'Unknown',
                ScanError TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Files_Size ON Files(FileSize);
            CREATE INDEX IF NOT EXISTS IX_Files_Extension ON Files(Extension);
            CREATE TABLE IF NOT EXISTS ScanRuns (
                ScanId INTEGER PRIMARY KEY,
                StartedUtc TEXT NOT NULL,
                FinishedUtc TEXT,
                Status TEXT NOT NULL,
                FileCount INTEGER NOT NULL DEFAULT 0,
                ErrorCount INTEGER NOT NULL DEFAULT 0
            );
            """;
        command.ExecuteNonQuery();
        if (version < 2)
        {
            command.CommandText = "ALTER TABLE Files ADD COLUMN LastSeenScanId INTEGER; ALTER TABLE ScanRuns ADD COLUMN SkippedCount INTEGER NOT NULL DEFAULT 0; REINDEX NOCASE;";
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ScanScopes (ScanId INTEGER NOT NULL, RootPath TEXT NOT NULL, PathPrefix TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS ScanIssues (IssueId INTEGER PRIMARY KEY, ScanId INTEGER NOT NULL,
                Path TEXT NOT NULL, PathPrefix TEXT NOT NULL, Status TEXT NOT NULL, Message TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_ScanIssues_ScanId ON ScanIssues(ScanId);
            CREATE INDEX IF NOT EXISTS IX_Files_LastSeen ON Files(LastSeenScanId);
            PRAGMA user_version = 2;
            UPDATE ScanRuns SET Status = 'Interrupted' WHERE Status = 'Running';
            """;
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }, cancellationToken);

    public Task<IndexSummary> GetSummaryAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM Files), (SELECT MAX(FinishedUtc) FROM ScanRuns WHERE Status = 'Completed');";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new IndexSummary(reader.GetInt64(0), reader.IsDBNull(1) ? null
            : DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture));
    }, cancellationToken);
}
