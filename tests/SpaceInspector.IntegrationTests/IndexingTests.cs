using Microsoft.Data.Sqlite;
using SpaceInspector.Core.Indexing;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.Database;
using SpaceInspector.Infrastructure.FileSystem;
using Xunit;

namespace SpaceInspector.IntegrationTests;

public sealed class IndexingTests : IDisposable
{
    private readonly string _area = Path.Combine(Path.GetTempPath(), "SpaceInspector.Tests", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_area, "files");
    private string DatabasePath => Path.Combine(_area, "data", "index.db");

    public IndexingTests() => Directory.CreateDirectory(Root);
    private async Task<SqliteIndexDatabase> Database()
    {
        var db = new SqliteIndexDatabase(DatabasePath);
        await db.InitializeAsync();
        return db;
    }
    private FileIndexingService Scanner(SqliteIndexDatabase db) => new(new MetadataOnlyFileSource(), db);
    private string Write(string relative, string content = "fixture")
    {
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task OverlappingScopesIndexOnceAndReopenWithMetadata()
    {
        Write("one.txt"); Write("child/two.bin", "");
        var db = await Database();
        var result = await Scanner(db).ScanAsync([Root, Path.Combine(Root, "child"), Root]);
        Assert.Equal("Completed", result.Status);
        Assert.Equal(2, result.Files);
        var reopened = new SqliteIndexDatabase(DatabasePath);
        var page = await reopened.GetPageAsync(0, 200);
        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Files, f => f.FileName == "two.bin" && f.FileSize == 0);
        Assert.All(page.Files, f => Assert.Equal("Normal", f.ScanStatus));
    }

    [Fact]
    public async Task ScopedRescanMarksMissingWithoutTouchingSiblingOrDeletingRows()
    {
        var gone = Write("100%_done/gone.txt");
        var kept = Write("100X_done/keep.txt");
        var changed = Write("100%_done/change.txt");
        var db = await Database();
        await Scanner(db).ScanAsync([Root]);
        var before = (await db.GetPageAsync(0, 200)).Files.Single(f => f.FullPath == changed);
        File.Delete(gone);
        File.WriteAllText(changed, "updated contents");
        await Scanner(db).ScanAsync([Path.GetDirectoryName(gone)!]);
        var page = await db.GetPageAsync(0, 200);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal("Missing", page.Files.Single(f => f.FullPath == gone).ScanStatus);
        Assert.Equal("Normal", page.Files.Single(f => f.FullPath == kept).ScanStatus);
        var after = page.Files.Single(f => f.FullPath == changed);
        Assert.Equal(before.FileId, after.FileId);
        Assert.Equal(new FileInfo(changed).Length, after.FileSize);
    }

    [Fact]
    public async Task CancellationKeepsPriorRowsAndCommitsObservedBatch()
    {
        Write("old.txt");
        var db = await Database();
        await Scanner(db).ScanAsync([Root]);
        using var cancellation = new CancellationTokenSource();
        IEnumerable<ScanObservation> Sequence()
        {
            yield return new(Metadata(Path.Combine(Root, "new.txt")), null);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        }
        var result = await new FileIndexingService(new FakeSource(Sequence), db).ScanAsync([Root], cancellationToken: cancellation.Token);
        Assert.Equal("Cancelled", result.Status);
        var page = await db.GetPageAsync(0, 200);
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Files, f => Assert.Equal("Normal", f.ScanStatus));
        Assert.Equal("Cancelled", Scalar("SELECT Status FROM ScanRuns ORDER BY ScanId DESC LIMIT 1"));
    }

    [Fact]
    public async Task InaccessibleSubtreeIsNotMarkedMissingAndErrorsPersist()
    {
        var denied = Write("denied/keep.txt");
        var gone = Write("gone.txt");
        var db = await Database();
        await Scanner(db).ScanAsync([Root]);
        var source = new FakeSource(() => [new(null, new(Path.GetDirectoryName(denied)!, "AccessDenied", "fixture denial"))]);
        var result = await new FileIndexingService(source, db).ScanAsync([Root]);
        Assert.Equal("CompletedWithIssues", result.Status);
        Assert.Equal(1, result.Errors);
        var page = await db.GetPageAsync(0, 200);
        Assert.Equal("AccessDenied", page.Files.Single(f => f.FullPath == denied).ScanStatus);
        Assert.Equal("Missing", page.Files.Single(f => f.FullPath == gone).ScanStatus);
        Assert.Single(await new SqliteIndexDatabase(DatabasePath).GetIssuesAsync(result.ScanId));
    }

    [Fact]
    public async Task DisconnectedOrMissingRootRetainsExistingIndex()
    {
        var path = Write("offline/keep.txt");
        var root = Path.GetDirectoryName(path)!;
        var db = await Database();
        await Scanner(db).ScanAsync([root]);
        Directory.Move(root, root + "-disconnected");
        var result = await Scanner(db).ScanAsync([root]);
        Assert.Equal(1, result.Errors);
        Assert.Equal("DeviceUnavailable", Assert.Single((await db.GetPageAsync(0, 200)).Files).ScanStatus);
    }

    [Fact]
    public async Task LockedContentIsIndexedWithoutOpeningIt()
    {
        var path = Write("locked.txt");
        var db = await Database();
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await Scanner(db).ScanAsync([Root]);
        Assert.Equal(1, result.Files);
        Assert.Equal(0, result.Errors);
    }

    [JunctionFact]
    public async Task JunctionAndExplicitChildAreSkippedWithoutFollowingTarget()
    {
        var target = Path.Combine(_area, "outside");
        Directory.CreateDirectory(Path.Combine(target, "child"));
        File.WriteAllText(Path.Combine(target, "child", "outside.txt"), "must not enumerate");
        var link = Path.Combine(Root, "junction");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/c mklink /J \"{link}\" \"{target}\""
        };
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        }
        try
        {
            var db = await Database();
            var result = await Scanner(db).ScanAsync([Root]);
            Assert.Equal(0, result.Files);
            Assert.Equal(1, result.Skipped);
            var child = await Scanner(db).ScanAsync([Path.Combine(link, "child")]);
            Assert.Equal(0, child.Files);
            Assert.Equal(1, child.Skipped);
            Assert.True(File.Exists(Path.Combine(target, "child", "outside.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task BatchProgressCountsErrorsAndSkippedSeparately()
    {
        var db = await Database();
        var source = new FakeSource(() => [new(Metadata(Path.Combine(Root, "a.txt")), null),
            new(null, new(Path.Combine(Root, "denied"), "AccessDenied", "denied")),
            new(null, new(Path.Combine(Root, "link"), "SkippedDirectory", "skipped"))]);
        var progress = new CaptureProgress();
        var result = await new FileIndexingService(source, db).ScanAsync([Root], progress);
        Assert.Equal(1, result.Files);
        Assert.Equal(1, result.Errors);
        Assert.Equal(1, result.Skipped);
        Assert.NotNull(progress.Last);
        Assert.Equal(result.Files, progress.Last.Files);
        Assert.Equal(result.Errors, progress.Last.Errors);
        Assert.Equal(result.Skipped, progress.Last.Skipped);
    }

    [Fact]
    public async Task ScannerRejectsConcurrentRunAndAllowsRunAfterCancellation()
    {
        var db = await Database();
        using var entered = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        IEnumerable<ScanObservation> Sequence()
        {
            entered.Set();
            cancellation.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            cancellation.Token.ThrowIfCancellationRequested();
            yield break;
        }
        var scanner = new FileIndexingService(new FakeSource(Sequence), db);
        var first = scanner.ScanAsync([Root], cancellationToken: cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ScanAsync([Root]));
        cancellation.Cancel();
        Assert.Equal("Cancelled", (await first).Status);
        Assert.Equal("Completed", (await Scanner(db).ScanAsync([Root])).Status);
    }

    [Fact]
    public async Task ExcludedDirectoryPreservesOldRowsWithoutVisitingIt()
    {
        Write("excluded/keep.txt");
        var db = await Database();
        await Scanner(db).ScanAsync([Root]);
        var source = new MetadataOnlyFileSource([Path.Combine(Root, "excluded")]);
        var result = await new FileIndexingService(source, db).ScanAsync([Root]);
        Assert.Equal(0, result.Files);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("NeedsRescan", Assert.Single((await db.GetPageAsync(0, 200)).Files).ScanStatus);
    }

    [Fact]
    public async Task PagingSpansBatchesAndReturnsEveryFileExactlyOnce()
    {
        for (var i = 0; i < 520; i++) Write($"{i:D4}.txt", "");
        var db = await Database();
        var result = await Scanner(db).ScanAsync([Root]);
        Assert.Equal(520, result.Files);
        var a = await db.GetPageAsync(0, 200);
        var b = await db.GetPageAsync(1, 200);
        var c = await db.GetPageAsync(2, 200);
        Assert.Equal(520, a.TotalCount);
        Assert.Equal(120, c.Files.Count);
        Assert.Equal(520, a.Files.Concat(b.Files).Concat(c.Files).Select(f => f.FileId).Distinct().Count());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.GetPageAsync(-1, 200));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.GetPageAsync(0, 1001));
    }

    [Fact]
    public async Task InterruptedRunRecoveredOnReopenWithoutLosingIndex()
    {
        Write("keep.txt");
        var db = await Database();
        await Scanner(db).ScanAsync([Root]);
        await db.BeginScanAsync([Root], CancellationToken.None);
        await new SqliteIndexDatabase(DatabasePath).InitializeAsync();
        Assert.Equal("Interrupted", Scalar("SELECT Status FROM ScanRuns ORDER BY ScanId DESC LIMIT 1"));
        Assert.Equal(1, (await db.GetSummaryAsync()).FileCount);
    }

    [Fact]
    public async Task UnicodePathCaseDoesNotCreateDuplicateIndexEntries()
    {
        var db = await Database();
        var path = Path.Combine(Root, "ÉTÉ.txt");
        var source = new FakeSource(() => [new(Metadata(path), null)]);
        await new FileIndexingService(source, db).ScanAsync([Root]);
        var second = new FakeSource(() => [new(Metadata(path.ToLowerInvariant()), null)]);
        await new FileIndexingService(second, db).ScanAsync([Root.ToLowerInvariant()]);
        var row = Assert.Single((await db.GetPageAsync(0, 200)).Files);
        Assert.Equal("Normal", row.ScanStatus);
    }

    [Fact]
    public async Task SchemaOneMigratesWithoutDroppingFiles()
    {
        var db = await Database();
        Write("keep.txt");
        await Scanner(db).ScanAsync([Root]);
        Execute("DROP INDEX IX_Files_LastSeen; ALTER TABLE Files DROP COLUMN LastSeenScanId; ALTER TABLE ScanRuns DROP COLUMN SkippedCount; PRAGMA user_version=1;");
        await db.InitializeAsync();
        Assert.Equal(1, (await db.GetSummaryAsync()).FileCount);
        Assert.Equal(2L, Scalar("PRAGMA user_version"));
        await Scanner(db).ScanAsync([Root]);
    }

    [Fact]
    public void ScopeNormalizationRejectsNetworkAndRespectsDirectoryBoundaries()
    {
        Assert.Throws<ArgumentException>(() => ScanScope.NormalizeRoots([@"\\server\share"]));
        Assert.Throws<ArgumentException>(() => ScanScope.NormalizeRoots(["relative"]));
        var roots = ScanScope.NormalizeRoots([Root, Path.Combine(Root, "child"), Root + "2"]);
        Assert.Equal(2, roots.Count);
    }

    [Theory]
    [InlineData(0x1000)]
    [InlineData(0x40000)]
    [InlineData(0x400000)]
    public void RecallAndOfflineFlagsAreRecognized(int attributes)
        => Assert.True(MetadataOnlyFileSource.IsCloudOnly((FileAttributes)attributes));

    private static FileMetadata Metadata(string path) => new(path, Path.GetFileName(path), ".txt",
        Path.GetDirectoryName(path)!, Path.GetPathRoot(path)!, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, false);
    private sealed class FakeSource(Func<IEnumerable<ScanObservation>> entries) : IFileMetadataSource
    {
        public IEnumerable<ScanObservation> Enumerate(IReadOnlyList<string> roots, CancellationToken cancellationToken) => entries();
    }
    private sealed class CaptureProgress : IProgress<ScanProgress>
    {
        public ScanProgress? Last { get; private set; }
        public void Report(ScanProgress value) => Last = value;
    }
    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
    private void Execute(string sql) => Scalar(sql);
    public void Dispose()
    {
        if (Directory.Exists(_area)) Directory.Delete(_area, true);
    }
}
