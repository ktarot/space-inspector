using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.Database;
using Xunit;

namespace SpaceInspector.IntegrationTests;

public sealed class SearchTests : IDisposable
{
    private readonly string _area = Path.Combine(Path.GetTempPath(), "SpaceInspector.Tests", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_area, "100%_files");
    private string DbPath => Path.Combine(_area, "index.db");
    private readonly DateTimeOffset _time = new(2026, 1, 10, 12, 0, 0, TimeSpan.FromHours(9));
    private async Task<SqliteIndexDatabase> Seed()
    {
        var db = new SqliteIndexDatabase(DbPath);
        await db.InitializeAsync();
        var id = await db.BeginScanAsync([Root], default);
        FileMetadata File(string name, long size, string? directory = null, int days = 0) => new(
            Path.Combine(directory ?? Root, name), name, Path.GetExtension(name), directory ?? Root,
            Path.GetPathRoot(Root)!, size, _time.AddDays(days), _time.AddDays(days), 0, null);
        await db.AppendAsync(id, [new(File("ÉTÉ 100%_报告.PDF", 1048576), null),
            new(File("photo.jpg", 2097152, days: 1), null), new(File("notes.txt", 0, days: -1), null),
            new(File("other.pdf", 1048576, Root + "-sibling"), null),
            new(null, new(Path.Combine(Root, "denied"), "AccessDenied", "fixture"))], default);
        await db.FinishScanAsync(new(id, "CompletedWithIssues", 4, 1, 0), default);
        return db;
    }

    [Fact]
    public async Task CombinesNameExtensionsSizeDatesAndScopeWithAnd()
    {
        var db = await Seed();
        var query = new FileSearch { Name = "été", Extensions = ["pdf", ".jpg"], Roots = [Root],
            MinimumBytes = 1048576, MaximumBytes = 1048576,
            CreatedFrom = _time, CreatedBefore = _time.AddDays(1), ModifiedFrom = _time, ModifiedBefore = _time.AddDays(1) };
        var result = await db.SearchAsync(query, 0, 200);
        Assert.Equal("ÉTÉ 100%_报告.PDF", Assert.Single(result.Files).FileName);
        Assert.Empty((await db.SearchAsync(query with { MaximumBytes = 1048575, MinimumBytes = null }, 0, 200)).Files);
    }

    [Theory]
    [InlineData("100%_", 1)]
    [InlineData("%", 1)]
    [InlineData("' OR 1=1 --", 0)]
    public async Task NamesAreLiteralAndSqlIsParameterized(string name, int expected)
    {
        var db = await Seed();
        Assert.Equal(expected, (await db.SearchAsync(new() { Name = name }, 0, 200)).TotalCount);
    }

    [Fact]
    public async Task ScopeBoundariesAndDateOffsetsAreCorrect()
    {
        var db = await Seed();
        Assert.Equal(3, (await db.SearchAsync(new() { Roots = [Root] }, 0, 200)).TotalCount);
        var atSameInstant = _time.ToUniversalTime();
        var result = await db.SearchAsync(new() { ModifiedFrom = atSameInstant, ModifiedBefore = atSameInstant.AddDays(1), Roots = [Root] }, 0, 200);
        Assert.Single(result.Files);
        Assert.Empty((await db.SearchAsync(new() { Extensions = ["exe"] }, 0, 200)).Files);
    }

    [Fact]
    public async Task SortsGloballyAndPaginatesWithStableTies()
    {
        var db = await Seed();
        var query = new FileSearch { Sort = FileSort.Size, Descending = true };
        var first = await db.SearchAsync(query, 0, 2);
        var second = await db.SearchAsync(query, 1, 2);
        Assert.Equal("photo.jpg", first.Files[0].FileName);
        Assert.Equal("notes.txt", second.Files[1].FileName);
        Assert.Equal(4, first.Files.Concat(second.Files).Select(f => f.FileId).Distinct().Count());
        foreach (var sort in Enum.GetValues<FileSort>())
            Assert.Equal(4, (await db.SearchAsync(new() { Sort = sort }, 0, 200)).Files.Count);
    }

    [Fact]
    public async Task ReopenedDatabaseProvidesLatestSummaryAndErrors()
    {
        await Seed();
        var reopened = new SqliteIndexDatabase(DbPath);
        await reopened.InitializeAsync();
        var history = await reopened.GetLatestScanAsync();
        Assert.NotNull(history);
        Assert.Equal(4, history.Files);
        Assert.Equal(1, history.Errors);
        Assert.Equal("CompletedWithIssues", history.Status);
        Assert.Single(await reopened.GetIssuesAsync(history.ScanId));
    }

    [Fact]
    public async Task InvalidQueriesDoNotReturnUnfilteredResults()
    {
        var db = await Seed();
        await Assert.ThrowsAsync<ArgumentException>(() => db.SearchAsync(new() { MinimumBytes = 2, MaximumBytes = 1 }, 0, 200));
        await Assert.ThrowsAsync<ArgumentException>(() => db.SearchAsync(new() { CreatedFrom = _time, CreatedBefore = _time }, 0, 200));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.SearchAsync(new(), -1, 200));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SearchAsync(new(), 0, 200, cancellation.Token));
    }

    public void Dispose() { if (Directory.Exists(_area)) Directory.Delete(_area, true); }
}
