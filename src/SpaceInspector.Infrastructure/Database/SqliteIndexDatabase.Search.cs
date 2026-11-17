using System.Globalization;
using SpaceInspector.Core.Indexing;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Infrastructure.Database;

public sealed partial class SqliteIndexDatabase : IFileSearchRepository
{
    public Task<IndexPage> SearchAsync(FileSearch search, int pageNumber, int pageSize, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        search.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(pageNumber);
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        connection.CreateFunction<string, string, bool>("contains_ignore_case", (value, term) => value.Contains(term, StringComparison.OrdinalIgnoreCase));
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var conditions = new List<string>();
        string Parameter(object value)
        {
            var key = "$p" + command.Parameters.Count;
            command.Parameters.AddWithValue(key, value);
            return key;
        }
        if (!string.IsNullOrWhiteSpace(search.Name)) conditions.Add("contains_ignore_case(FileName," + Parameter(search.Name) + ")");
        if (search.Extensions.Count > 0)
            conditions.Add("Extension COLLATE NOCASE IN (" + string.Join(',', search.Extensions.Select(e => Parameter("." + e.Trim().TrimStart('.')))) + ")");
        if (search.Roots.Count > 0)
        {
            var roots = ScanScope.NormalizeRoots(search.Roots);
            conditions.Add("(" + string.Join(" OR ", roots.Select(root =>
            {
                var path = Parameter(root);
                var prefix = Parameter(ScanScope.Prefix(root));
                return $"(FullPath={path} COLLATE NOCASE OR substr(FullPath,1,length({prefix}))={prefix} COLLATE NOCASE)";
            })) + ")");
        }
        if (search.MinimumBytes is { } min) conditions.Add("FileSize >= " + Parameter(min));
        if (search.MaximumBytes is { } max) conditions.Add("FileSize <= " + Parameter(max));
        void DateCondition(string column, string comparison, DateTimeOffset? date)
        {
            if (date is { } value) conditions.Add($"julianday({column}) {comparison} julianday({Parameter(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))})");
        }
        DateCondition("CreationTimeUtc", ">=", search.CreatedFrom);
        DateCondition("CreationTimeUtc", "<", search.CreatedBefore);
        DateCondition("ModifiedTimeUtc", ">=", search.ModifiedFrom);
        DateCondition("ModifiedTimeUtc", "<", search.ModifiedBefore);
        var where = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        command.CommandText = "SELECT COUNT(*) FROM Files" + where;
        var count = Convert.ToInt64(command.ExecuteScalar());
        var sort = search.Sort switch
        {
            FileSort.Name => "FileName COLLATE NOCASE", FileSort.Size => "FileSize",
            FileSort.Modified => "julianday(ModifiedTimeUtc)", FileSort.Created => "julianday(CreationTimeUtc)",
            FileSort.Path => "FullPath COLLATE NOCASE", _ => "FileId"
        };
        command.CommandText = "SELECT FileId,FileName,FullPath,FileSize,ModifiedTimeUtc,ScanStatus,IsCloudPlaceholder FROM Files" + where
            + " ORDER BY " + sort + (search.Descending ? " DESC" : " ASC") + ",FileId ASC LIMIT $limit OFFSET $offset";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)pageNumber * pageSize);
        using var reader = command.ExecuteReader();
        var files = new List<IndexedFile>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetBoolean(6)));
        }
        return new IndexPage(files, count, pageNumber, pageSize);
    }, cancellationToken);

    public Task<ScanHistory?> GetLatestScanAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ScanId,Status,StartedUtc,FinishedUtc,FileCount,ErrorCount,SkippedCount FROM ScanRuns ORDER BY ScanId DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new ScanHistory(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)) : null;
    }, cancellationToken);
}
