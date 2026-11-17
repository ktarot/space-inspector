using Microsoft.Data.Sqlite;

namespace SpaceInspector.Infrastructure.Database;

// Command.Cancel is a no-op in Microsoft.Data.Sqlite. Poll inside the SQLite VM instead.
internal sealed class SqliteQueryCancellation : IDisposable
{
    private readonly SqliteConnection _connection;
    public SqliteQueryCancellation(SqliteConnection connection, CancellationToken token)
    {
        _connection = connection;
        SQLitePCL.raw.sqlite3_progress_handler(connection.Handle, 1000, _ => token.IsCancellationRequested ? 1 : 0, null);
    }

    public void Dispose() => SQLitePCL.raw.sqlite3_progress_handler(_connection.Handle, 0, null, null);
}
