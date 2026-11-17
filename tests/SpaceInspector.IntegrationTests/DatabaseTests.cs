using Microsoft.Data.Sqlite;
using SpaceInspector.Infrastructure.Database;
using Xunit;

namespace SpaceInspector.IntegrationTests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SpaceInspector.Tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "index.db");

    [Fact]
    public async Task InitializationCreatesEmptyPersistentIndex()
    {
        await new SqliteIndexDatabase(DatabasePath).InitializeAsync();
        var summary = await new SqliteIndexDatabase(DatabasePath).GetSummaryAsync();
        Assert.True(File.Exists(DatabasePath));
        Assert.Equal(0, summary.FileCount);
        Assert.Null(summary.LastScanUtc);
    }

    [Fact]
    public async Task RepeatedInitializationPreservesData()
    {
        var database = new SqliteIndexDatabase(DatabasePath);
        await database.InitializeAsync();
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Files(FullPath,FileName,ParentDirectory,Drive) VALUES('X:\\fixture.txt','fixture.txt','X:\\','X:\\');";
            command.ExecuteNonQuery();
        }
        await database.InitializeAsync();
        Assert.Equal(1, (await database.GetSummaryAsync()).FileCount);
    }

    [Fact]
    public async Task FutureSchemaIsRejectedWithoutDowngrade()
    {
        var database = new SqliteIndexDatabase(DatabasePath);
        await database.InitializeAsync();
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.InitializeAsync());
        using var verify = Open();
        using var query = verify.CreateCommand();
        query.CommandText = "PRAGMA user_version;";
        Assert.Equal(99L, query.ExecuteScalar());
    }

    [Fact]
    public async Task CancelledInitializationDoesNotCreateDatabase()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqliteIndexDatabase(DatabasePath).InitializeAsync(cancellation.Token));
        Assert.False(File.Exists(DatabasePath));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
