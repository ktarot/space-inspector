using Microsoft.Data.Sqlite;
using SpaceInspector.Infrastructure.Database;
using Xunit;

namespace SpaceInspector.IntegrationTests;

public class SchemaMigrationTests
{
    [Fact]
    public async Task VersionTwoUpgradeBacksUpAndPreservesLegacyMetadata()
    {
        var area = Path.Combine(Path.GetTempPath(), "SpaceInspector.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(area, "index.db");
        try
        {
            var db = new SqliteIndexDatabase(path); await db.InitializeAsync();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP INDEX IX_Files_IndexKey;
                    ALTER TABLE Files DROP COLUMN IndexKey;
                    ALTER TABLE Files DROP COLUMN VolumeRelativePath;
                    ALTER TABLE Files DROP COLUMN DuplicateRunId;
                    ALTER TABLE Files DROP COLUMN DuplicateError;
                    ALTER TABLE ScanScopes DROP COLUMN VolumeIdentifier;
                    DROP TABLE Volumes; DROP TABLE DuplicateRuns; DROP TABLE DuplicateWork;
                    INSERT INTO Files(FullPath,FileName,ParentDirectory,Drive,FileSize,ScanStatus) VALUES('X:\a.txt','a.txt','X:\','X:\',42,'Normal');
                    CREATE UNIQUE INDEX LegacyPath ON Files(FullPath);
                    PRAGMA user_version=2;
                    """;
                command.ExecuteNonQuery();
            }
            await db.InitializeAsync(); await db.InitializeAsync();
            var row = Assert.Single((await db.GetPageAsync(0, 100)).Files);
            Assert.Equal(42, row.FileSize); Assert.Null(row.VolumeIdentifier);
            var backups = Directory.GetFiles(area, "*.schema2-backup-*.db");
            Assert.Single(backups);
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backups[0], Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            backup.Open(); using var verify = backup.CreateCommand(); verify.CommandText = "PRAGMA user_version";
            Assert.Equal(2L, verify.ExecuteScalar());
        }
        finally { if (Directory.Exists(area)) Directory.Delete(area, true); }
    }
}
