using Microsoft.Data.Sqlite;

namespace SpaceInspector.Infrastructure.Database;

public sealed partial class SqliteIndexDatabase
{
    public async Task RecoverWorkResultAsync()
    {
        var run = await Task.Run(() =>
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT RunId FROM DuplicateRuns WHERE Status='Running' ORDER BY RunId DESC LIMIT 1";
            var value = command.ExecuteScalar(); return value is null ? (long?)null : Convert.ToInt64(value);
        });
        if (run is { } id) await FinishDuplicatesAsync(new(id, "Interrupted", 0, 0), Core.Models.DuplicateLevel.FullHash, default);
    }

    public Task BackupAsync(string path, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        using var source = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabaseFilePath(path), Pooling = false }.ToString());
        target.Open(); source.BackupDatabase(target);
        token.ThrowIfCancellationRequested();
    }, token);
}
