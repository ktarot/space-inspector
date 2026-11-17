using Microsoft.Data.Sqlite;
using SpaceInspector.Infrastructure.Database;
using Xunit;

namespace SpaceInspector.IntegrationTests;

public class QueryCancellationTests
{
    [Fact]
    public async Task CancellationInterruptsExecutingSqliteVmAndConnectionRemainsUsable()
    {
        using var started = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var query = Task.Run(() =>
        {
            using var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            connection.CreateFunction("signal_started", () => { started.Set(); return 1; });
            using var command = connection.CreateCommand();
            command.CommandText = "WITH RECURSIVE n(x) AS (SELECT signal_started() UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT sum(x) FROM n";
            using (new SqliteQueryCancellation(connection, cancellation.Token))
            {
                var error = Assert.Throws<SqliteException>(() => command.ExecuteScalar());
                Assert.Equal(9, error.SqliteErrorCode);
            }
            command.CommandText = "SELECT 42";
            Assert.Equal(42L, command.ExecuteScalar());
        });
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "Query did not start.");
            cancellation.Cancel();
            await query.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { cancellation.Cancel(); }
    }
}
