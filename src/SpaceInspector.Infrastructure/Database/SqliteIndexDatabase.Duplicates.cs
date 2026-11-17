using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Infrastructure.Database;

public sealed partial class SqliteIndexDatabase : IDuplicateRepository
{
    public Task<long> BeginDuplicatesAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        using var cancellation = new SqliteQueryCancellation(connection, cancellationToken);
        command.CommandText = "UPDATE Files SET PartialHash=NULL,FullHash=NULL,DuplicateGroupId=NULL,DuplicateStatus='Unverified',DuplicateError=NULL,DuplicateRunId=NULL; INSERT INTO DuplicateRuns(StartedUtc,Status) VALUES($now,'Running'); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var id = Convert.ToInt64(command.ExecuteScalar());
        command.Parameters.Clear(); command.Parameters.AddWithValue("$run", id);
        command.CommandText = "DELETE FROM DuplicateWork; INSERT INTO DuplicateWork SELECT $run,FileId FROM Files WHERE ScanStatus='Normal' AND FileSize IN (SELECT FileSize FROM Files WHERE ScanStatus='Normal' GROUP BY FileSize HAVING COUNT(*)>1);";
        command.ExecuteNonQuery();
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit(); return id;
    }, cancellationToken);

    public Task<IReadOnlyList<DuplicateCandidate>> GetDuplicateCandidatesAsync(long afterId, int count, bool fullHash, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<DuplicateCandidate>>(() =>
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        using var cancellation = new SqliteQueryCancellation(connection, cancellationToken);
        command.CommandText = "SELECT f.FileId,FullPath,FileSize,ModifiedTimeUtc,VolumeIdentifier,FileSystemIdentifier FROM DuplicateWork w JOIN Files f ON f.FileId=w.FileId WHERE w.RunId=(SELECT MAX(RunId) FROM DuplicateRuns WHERE Status='Running') AND ScanStatus='Normal' AND f.FileId>$after AND "
            + (fullHash ? "PartialHash IS NOT NULL AND EXISTS(SELECT 1 FROM Files other WHERE other.FileSize=f.FileSize AND other.PartialHash=f.PartialHash AND other.FileId<>f.FileId)"
                : "1=1")
            + " ORDER BY f.FileId LIMIT $limit";
        command.Parameters.AddWithValue("$after", afterId); command.Parameters.AddWithValue("$limit", Math.Clamp(count, 1, 200));
        return ReadCandidates(command, cancellationToken);
    }, cancellationToken);

    private static IReadOnlyList<DuplicateCandidate> ReadCandidates(Microsoft.Data.Sqlite.SqliteCommand command, CancellationToken token)
    {
        using var reader = command.ExecuteReader(); var files = new List<DuplicateCandidate>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            files.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return files;
    }

    public Task StoreFingerprintAsync(long runId, DuplicateCandidate file, FileFingerprint fingerprint, bool fullHash, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET DuplicateRunId=$run,DuplicateStatus=$status,DuplicateError=$error,FileSystemIdentifier=$physical,"
            + (fullHash ? "FullHash=$hash,PartialHash=CASE WHEN $status='Unverified' THEN NULL ELSE PartialHash END" : "PartialHash=$hash,FullHash=NULL")
            + " WHERE FileId=$id AND FileSize=$size AND ModifiedTimeUtc=$modified AND (VolumeIdentifier=$volume OR (VolumeIdentifier IS NULL AND $volume IS NULL))";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$id", file.FileId);
        command.Parameters.AddWithValue("$size", file.Size); command.Parameters.AddWithValue("$modified", file.ModifiedUtc);
        command.Parameters.AddWithValue("$volume", (object?)file.VolumeIdentifier ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", fingerprint.Status); command.Parameters.AddWithValue("$error", (object?)fingerprint.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$physical", (object?)fingerprint.PhysicalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", (object?)fingerprint.Hash ?? DBNull.Value);
        command.ExecuteNonQuery();
    }, cancellationToken);

    public Task<IReadOnlyList<DuplicateCandidate>> GetHashedFilesAsync(long runId, long afterId, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<DuplicateCandidate>>(() =>
    {
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT FileId,FullPath,FileSize,ModifiedTimeUtc,VolumeIdentifier,FileSystemIdentifier FROM Files WHERE DuplicateRunId=$run AND FullHash IS NOT NULL AND FileId>$after ORDER BY FileId LIMIT 100";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$after", afterId);
        return ReadCandidates(command, cancellationToken);
    }, cancellationToken);

    public Task FinishDuplicatesAsync(DuplicateResult result, DuplicateLevel level, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", result.RunId);
        if (result.Status == "Completed")
        {
            command.CommandText = """
                UPDATE Files SET DuplicateStatus='NotDuplicate',DuplicateGroupId=NULL WHERE DuplicateRunId=$run AND PartialHash IS NOT NULL;
                UPDATE Files SET DuplicateStatus='SuspectedDuplicate',DuplicateGroupId='size|' || FileSize
                    WHERE DuplicateRunId=$run AND DuplicateStatus='SuspectedDuplicate';
                UPDATE Files SET DuplicateStatus='StrongCandidate',DuplicateGroupId='partial|' || FileSize || '|' || PartialHash
                    WHERE DuplicateRunId=$run AND PartialHash IS NOT NULL AND EXISTS(SELECT 1 FROM Files other
                        WHERE other.DuplicateRunId=$run AND other.FileSize=Files.FileSize AND other.PartialHash=Files.PartialHash AND other.FileId<>Files.FileId);
                """;
            command.ExecuteNonQuery();
            if (level == DuplicateLevel.FullHash)
            {
                command.CommandText = """
                    UPDATE Files SET DuplicateStatus='NotDuplicate',DuplicateGroupId=NULL WHERE DuplicateRunId=$run AND FullHash IS NOT NULL;
                    UPDATE Files SET DuplicateStatus='ConfirmedDuplicate',DuplicateGroupId='full|' || FileSize || '|' || FullHash
                        WHERE DuplicateRunId=$run AND FullHash IS NOT NULL AND FileSystemIdentifier IS NOT NULL
                        AND EXISTS(SELECT 1 FROM Files other WHERE other.DuplicateRunId=$run AND other.FileSize=Files.FileSize
                            AND other.FullHash=Files.FullHash AND other.FileId<>Files.FileId AND other.FileSystemIdentifier IS NOT NULL);
                    """;
                command.ExecuteNonQuery();
            }
        }
        else
        {
            command.CommandText = "UPDATE Files SET DuplicateGroupId=NULL,DuplicateStatus='Unverified' WHERE DuplicateRunId=$run;";
            command.ExecuteNonQuery();
        }
        command.CommandText = "UPDATE DuplicateRuns SET FinishedUtc=$now,Status=$status,Processed=$processed,Unverified=$unverified WHERE RunId=$run";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", result.Status); command.Parameters.AddWithValue("$processed", result.Processed); command.Parameters.AddWithValue("$unverified", result.Unverified);
        command.ExecuteNonQuery(); transaction.Commit();
    }, cancellationToken);

    public Task<IReadOnlyList<DuplicateGroup>> GetDuplicateGroupsAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DuplicateGroup>>(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageNumber);
        using var connection = Open(); using var command = connection.CreateCommand();
        using var cancellation = new SqliteQueryCancellation(connection, cancellationToken);
        command.CommandText = """
            SELECT * FROM (
                SELECT DuplicateGroupId,DuplicateStatus,COUNT(*),COUNT(DISTINCT FileSystemIdentifier),FileSize
                FROM Files WHERE DuplicateGroupId IS NOT NULL GROUP BY DuplicateGroupId HAVING COUNT(*)>1
                UNION ALL SELECT 'unverified','Unverified',COUNT(*),0,0 FROM Files
                    WHERE DuplicateRunId IS NOT NULL AND DuplicateStatus='Unverified' HAVING COUNT(*)>0
            ) ORDER BY 2,5 DESC,1 LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(pageSize, 1, 200)); command.Parameters.AddWithValue("$offset", (long)pageNumber * pageSize);
        using var reader = command.ExecuteReader(); var groups = new List<DuplicateGroup>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = reader.GetInt64(2); var data = reader.GetInt64(3); var size = reader.GetInt64(4);
            groups.Add(new(reader.GetString(0), reader.GetString(1), count, data,
                data > 1 && size <= long.MaxValue / (data - 1) ? (data - 1) * size : 0));
        }
        return groups;
    }, cancellationToken);

    public Task<IReadOnlyList<DuplicateMember>> GetDuplicateMembersAsync(string groupId, int pageNumber, int pageSize, CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<DuplicateMember>>(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageNumber);
        using var connection = Open(); using var command = connection.CreateCommand();
        using var cancellation = new SqliteQueryCancellation(connection, cancellationToken);
        command.CommandText = "SELECT FileId,FullPath,FileSize,DuplicateStatus,DuplicateError,VolumeIdentifier FROM Files WHERE DuplicateGroupId=$group OR ($group='unverified' AND DuplicateRunId IS NOT NULL AND DuplicateStatus='Unverified') ORDER BY FileId LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$group", groupId); command.Parameters.AddWithValue("$limit", Math.Clamp(pageSize, 1, 200));
        command.Parameters.AddWithValue("$offset", (long)pageNumber * pageSize);
        using var reader = command.ExecuteReader(); var files = new List<DuplicateMember>();
        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); files.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5))); }
        return files;
    }, cancellationToken);
}
