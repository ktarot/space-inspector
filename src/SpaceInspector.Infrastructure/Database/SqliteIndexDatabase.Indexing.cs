using Microsoft.Data.Sqlite;
using SpaceInspector.Core.Indexing;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Infrastructure.Database;

public sealed partial class SqliteIndexDatabase
{
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.CreateCollation("NOCASE", (left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        return connection;
    }

    public Task<long> BeginScanAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM ScanRuns WHERE Status='Running';";
        if (Convert.ToInt64(command.ExecuteScalar()) > 0) throw new InvalidOperationException("진행 중인 검사가 있습니다.");
        command.CommandText = "INSERT INTO ScanRuns(StartedUtc,Status) VALUES($now,'Running'); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var id = Convert.ToInt64(command.ExecuteScalar());
        foreach (var root in roots)
        {
            var volume = _volumes?.GetIdentity(root);
            if (volume is not null) RegisterVolume(command, volume, Path.GetPathRoot(root)!);
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO ScanScopes(ScanId,RootPath,PathPrefix,VolumeIdentifier) VALUES($id,$root,$prefix,$volume);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$prefix", ScanScope.Prefix(root));
            command.Parameters.AddWithValue("$volume", (object?)volume ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return id;
    }, cancellationToken);

    public Task AppendAsync(long scanId, IReadOnlyList<ScanObservation> batch, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var observation in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", scanId);
            if (observation.File is { } file)
            {
                if (file.VolumeIdentifier is not null)
                {
                    command.CommandText = "SELECT COUNT(*) FROM ScanScopes WHERE ScanId=$id AND VolumeIdentifier=$volume";
                    command.Parameters.AddWithValue("$volume", file.VolumeIdentifier);
                    if (Convert.ToInt64(command.ExecuteScalar()) == 0) throw new IOException("검사 시작 이후 장치 식별자가 달라졌습니다. 다시 검사하세요.");
                    command.Parameters.RemoveAt("$volume");
                }
                var key = file.VolumeIdentifier is null ? "path|" + file.FullPath : "volume|" + file.VolumeIdentifier + "|" + file.VolumeRelativePath;
                if (file.VolumeIdentifier is not null)
                {
                    command.CommandText = "UPDATE Files SET IndexKey=$key WHERE VolumeIdentifier IS NULL AND FullPath=$path COLLATE NOCASE AND NOT EXISTS(SELECT 1 FROM Files WHERE IndexKey=$key);";
                    command.Parameters.AddWithValue("$key", key);
                    command.Parameters.AddWithValue("$path", file.FullPath);
                    command.ExecuteNonQuery();
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("$id", scanId);
                }
                command.CommandText = """
                    INSERT INTO Files(FullPath,FileName,Extension,ParentDirectory,Drive,FileSize,CreationTimeUtc,
                        ModifiedTimeUtc,Attributes,IsCloudPlaceholder,ScanTimeUtc,ScanStatus,LastSeenScanId,IndexKey,VolumeIdentifier,VolumeRelativePath)
                    VALUES($path,$name,$ext,$parent,$drive,$size,$created,$modified,$attrs,$cloud,$now,'Normal',$id,$key,$volume,$relative)
                    ON CONFLICT(IndexKey) DO UPDATE SET FullPath=excluded.FullPath,FileName=excluded.FileName,Extension=excluded.Extension,
                        ParentDirectory=excluded.ParentDirectory,Drive=excluded.Drive,FileSize=excluded.FileSize,
                        CreationTimeUtc=excluded.CreationTimeUtc,ModifiedTimeUtc=excluded.ModifiedTimeUtc,
                        Attributes=excluded.Attributes,IsCloudPlaceholder=excluded.IsCloudPlaceholder,
                        ScanTimeUtc=excluded.ScanTimeUtc,ScanStatus='Normal',ScanError=NULL,LastSeenScanId=$id,
                        VolumeIdentifier=excluded.VolumeIdentifier,VolumeRelativePath=excluded.VolumeRelativePath,
                        FileSystemIdentifier=NULL,DuplicateRunId=NULL,DuplicateError=NULL,
                        PartialHash=NULL,FullHash=NULL,DuplicateGroupId=NULL,DuplicateStatus='Unverified';
                    """;
                command.Parameters.AddWithValue("$path", file.FullPath);
                command.Parameters.AddWithValue("$name", file.FileName);
                command.Parameters.AddWithValue("$ext", file.Extension);
                command.Parameters.AddWithValue("$parent", file.ParentDirectory);
                command.Parameters.AddWithValue("$drive", file.Drive);
                command.Parameters.AddWithValue("$size", file.FileSize);
                command.Parameters.AddWithValue("$created", file.CreationTimeUtc.ToString("O"));
                command.Parameters.AddWithValue("$modified", file.ModifiedTimeUtc.ToString("O"));
                command.Parameters.AddWithValue("$attrs", file.Attributes);
                command.Parameters.AddWithValue("$cloud", file.IsCloudPlaceholder is { } cloud ? (cloud ? 1 : 0) : DBNull.Value);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$volume", (object?)file.VolumeIdentifier ?? DBNull.Value);
                command.Parameters.AddWithValue("$relative", (object?)file.VolumeRelativePath ?? DBNull.Value);
            }
            else if (observation.Issue is { } issue)
            {
                command.CommandText = "INSERT INTO ScanIssues(ScanId,Path,PathPrefix,Status,Message) VALUES($id,$path,$prefix,$status,$message);";
                command.Parameters.AddWithValue("$path", issue.Path);
                command.Parameters.AddWithValue("$prefix", ScanScope.Prefix(issue.Path));
                command.Parameters.AddWithValue("$status", issue.Status);
                command.Parameters.AddWithValue("$message", issue.Message);
            }
            else continue;
            command.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }, cancellationToken);

    public Task FinishScanAsync(ScanResult result, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", result.ScanId);
        // A successful walk can reconcile only within selected scopes, excluding every inaccessible/skipped subtree.
        // substr + NOCASE avoids LIKE wildcard surprises in folder names such as "100%_done".
        if (result.Status is "Completed" or "CompletedWithIssues")
        {
            command.CommandText = """
                UPDATE Files SET ScanStatus='Missing', PartialHash=NULL,FullHash=NULL,DuplicateGroupId=NULL,DuplicateStatus='Unverified'
                WHERE (LastSeenScanId IS NULL OR LastSeenScanId <> $id)
                AND EXISTS (SELECT 1 FROM ScanScopes s WHERE s.ScanId=$id
                    AND (Files.VolumeIdentifier=s.VolumeIdentifier OR (Files.VolumeIdentifier IS NULL AND s.VolumeIdentifier IS NULL))
                    AND substr(Files.FullPath,1,length(s.PathPrefix))=s.PathPrefix COLLATE NOCASE)
                AND NOT EXISTS (SELECT 1 FROM ScanIssues i WHERE i.ScanId=$id
                    AND (Files.FullPath=i.Path COLLATE NOCASE OR substr(Files.FullPath,1,length(i.PathPrefix))=i.PathPrefix COLLATE NOCASE));
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            UPDATE Files SET ScanStatus='NeedsRescan',DuplicateStatus='Unverified',PartialHash=NULL,FullHash=NULL,DuplicateGroupId=NULL
                WHERE EXISTS (SELECT 1 FROM ScanIssues i WHERE i.ScanId=$id
                AND EXISTS(SELECT 1 FROM ScanScopes s WHERE s.ScanId=$id AND (s.VolumeIdentifier=Files.VolumeIdentifier OR s.VolumeIdentifier IS NULL))
                AND (Files.FullPath=i.Path COLLATE NOCASE OR substr(Files.FullPath,1,length(i.PathPrefix))=i.PathPrefix COLLATE NOCASE));
            UPDATE Files SET ScanStatus='AccessDenied' WHERE EXISTS (SELECT 1 FROM ScanIssues i
                WHERE i.ScanId=$id AND i.Status='AccessDenied'
                AND EXISTS(SELECT 1 FROM ScanScopes s WHERE s.ScanId=$id AND (s.VolumeIdentifier=Files.VolumeIdentifier OR s.VolumeIdentifier IS NULL))
                AND (Files.FullPath=i.Path COLLATE NOCASE OR substr(Files.FullPath,1,length(i.PathPrefix))=i.PathPrefix COLLATE NOCASE));
            UPDATE Files SET ScanStatus='DeviceUnavailable' WHERE EXISTS (SELECT 1 FROM ScanIssues i
                WHERE i.ScanId=$id AND i.Status='Unavailable'
                AND EXISTS(SELECT 1 FROM ScanScopes s WHERE s.ScanId=$id AND (s.VolumeIdentifier=Files.VolumeIdentifier OR s.VolumeIdentifier IS NULL))
                AND (Files.FullPath=i.Path COLLATE NOCASE OR substr(Files.FullPath,1,length(i.PathPrefix))=i.PathPrefix COLLATE NOCASE));
            UPDATE ScanRuns SET FinishedUtc=$now,Status=$status,FileCount=$files,ErrorCount=$errors,SkippedCount=$skipped WHERE ScanId=$id;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", result.Status);
        command.Parameters.AddWithValue("$files", result.Files);
        command.Parameters.AddWithValue("$errors", result.Errors);
        command.Parameters.AddWithValue("$skipped", result.Skipped);
        command.ExecuteNonQuery();
        transaction.Commit();
    }, cancellationToken);

    public Task<IndexPage> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageNumber);
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM Files;";
        var count = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "SELECT FileId,FileName,FullPath,FileSize,ModifiedTimeUtc,ScanStatus,IsCloudPlaceholder,VolumeIdentifier FROM Files ORDER BY FileId LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)pageNumber * pageSize);
        using var reader = command.ExecuteReader();
        var files = new List<IndexedFile>(pageSize);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return new IndexPage(files, count, pageNumber, pageSize);
    }, cancellationToken);

    public Task<IReadOnlyList<ScanIssue>> GetIssuesAsync(long scanId, int limit = 100, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<ScanIssue>>(() =>
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Path,Status,Message FROM ScanIssues WHERE ScanId=$id ORDER BY IssueId LIMIT $limit;";
        command.Parameters.AddWithValue("$id", scanId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var issues = new List<ScanIssue>();
        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); issues.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2))); }
        return issues;
    }, cancellationToken);

    private static void RegisterVolume(SqliteCommand command, string identity, string root)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$volume", identity);
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.CommandText = """
            UPDATE Files SET ScanStatus='DeviceUnavailable',DuplicateStatus='Unverified',FullHash=NULL,PartialHash=NULL,DuplicateGroupId=NULL
                WHERE Drive=$root COLLATE NOCASE AND VolumeIdentifier IS NOT NULL AND VolumeIdentifier<>$volume;
            UPDATE Files SET FullPath=$root || VolumeRelativePath,
                ParentDirectory=$root || substr(VolumeRelativePath,1,length(VolumeRelativePath)-length(FileName)-1),
                Drive=$root,ScanStatus='NeedsRescan',DuplicateStatus='Unverified',FullHash=NULL,PartialHash=NULL,DuplicateGroupId=NULL
                WHERE VolumeIdentifier=$volume AND Drive<>$root COLLATE NOCASE AND VolumeRelativePath IS NOT NULL;
            INSERT INTO Volumes VALUES($volume,$root,$now) ON CONFLICT(VolumeIdentifier) DO UPDATE SET RootPath=$root,LastSeenUtc=$now;
            """;
        command.ExecuteNonQuery();
    }
}
