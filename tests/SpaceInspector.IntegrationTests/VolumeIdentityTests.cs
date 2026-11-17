using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.Database;
using SpaceInspector.Infrastructure.FileSystem;
using Xunit;

namespace SpaceInspector.IntegrationTests;

public sealed class VolumeIdentityTests : IDisposable
{
    private readonly string _area = Path.Combine(Path.GetTempPath(), "SpaceInspector.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVolumes _volumes = new();
    private async Task<SqliteIndexDatabase> Database()
    {
        var db = new SqliteIndexDatabase(Path.Combine(_area, "index.db"), _volumes);
        await db.InitializeAsync(); await db.InitializeAsync(); return db;
    }
    private static FileMetadata File(string drive, string volume) => new(drive + "folder\\a.txt", "a.txt", ".txt", drive + "folder", drive,
        10, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, false, volume, @"folder\a.txt");
    private static async Task Scan(SqliteIndexDatabase db, string root, FileMetadata file)
    {
        var run = await db.BeginScanAsync([root], default);
        await db.AppendAsync(run, [new(file, null)], default);
        await db.FinishScanAsync(new(run, "Completed", 1, 0, 0), default);
    }
    [Fact]
    public async Task ChangedDriveLetterReusesIndexIdentityAndRewritesPaths()
    {
        _volumes.Items[@"X:\"] = "volume-A";
        var db = await Database(); await Scan(db, @"X:\", File(@"X:\", "volume-A"));
        var first = Assert.Single((await db.GetPageAsync(0, 100)).Files);
        _volumes.Items.Remove(@"X:\"); _volumes.Items[@"Y:\"] = "volume-A";
        await Scan(db, @"Y:\", File(@"Y:\", "volume-A"));
        var second = Assert.Single((await db.GetPageAsync(0, 100)).Files);
        Assert.Equal(first.FileId, second.FileId); Assert.Equal(@"Y:\folder\a.txt", second.FullPath);
        Assert.Equal("volume-A", second.VolumeIdentifier); Assert.Equal("Normal", second.ScanStatus);
    }
    [Fact]
    public async Task DifferentDeviceReusingLetterDoesNotOverwriteOldDevice()
    {
        _volumes.Items[@"X:\"] = "volume-A";
        var db = await Database(); await Scan(db, @"X:\", File(@"X:\", "volume-A"));
        _volumes.Items[@"X:\"] = "volume-B";
        await Scan(db, @"X:\", File(@"X:\", "volume-B"));
        var rows = (await db.GetPageAsync(0, 100)).Files;
        Assert.Equal(2, rows.Count);
        Assert.Equal("DeviceUnavailable", rows.Single(f => f.VolumeIdentifier == "volume-A").ScanStatus);
        Assert.Equal("Normal", rows.Single(f => f.VolumeIdentifier == "volume-B").ScanStatus);
    }
    [Fact]
    public async Task LegacyPathIsBoundOnlyWhenObservedAndPreservesFileId()
    {
        var db = await Database(); var file = File(@"X:\", "volume-A");
        await Scan(db, @"X:\", file with { VolumeIdentifier = null, VolumeRelativePath = null });
        var before = Assert.Single((await db.GetPageAsync(0, 100)).Files);
        Assert.Null(before.VolumeIdentifier);
        _volumes.Items[@"X:\"] = "volume-A";
        await Scan(db, @"X:\", file);
        var after = Assert.Single((await db.GetPageAsync(0, 100)).Files);
        Assert.Equal(before.FileId, after.FileId); Assert.Equal("volume-A", after.VolumeIdentifier);
    }
    [Fact]
    public void WindowsVolumeIdentityIsStableAndRejectsNetworkPath()
    {
        var service = new WindowsVolumeIdentityService();
        var identity = service.GetIdentity(_area);
        Assert.NotNull(identity); Assert.Equal(identity, service.GetIdentity(Path.GetPathRoot(_area)!));
        Assert.Null(service.GetIdentity(@"\\server\share"));
    }
    [Fact]
    public async Task DeviceChangedAfterScanStartCannotWriteNewDeviceIntoOldScope()
    {
        _volumes.Items[@"X:\"] = "volume-A"; var db = await Database();
        var run = await db.BeginScanAsync([@"X:\"], default);
        await Assert.ThrowsAsync<IOException>(() => db.AppendAsync(run, [new(File(@"X:\", "volume-B"), null)], default));
        Assert.Empty((await db.GetPageAsync(0, 100)).Files);
        await db.FinishScanAsync(new(run, "Failed", 0, 1, 0), default);
    }
    private sealed class FakeVolumes : IVolumeIdentityService
    {
        public Dictionary<string, string> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? GetIdentity(string rootPath) => Items.GetValueOrDefault(Path.GetPathRoot(rootPath)!);
    }
    public void Dispose() { if (Directory.Exists(_area)) Directory.Delete(_area, true); }
}
