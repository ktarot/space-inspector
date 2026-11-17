using System.Runtime.InteropServices;
using SpaceInspector.Core.DuplicateDetection;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Indexing;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.Database;
using SpaceInspector.Infrastructure.FileSystem;
using Xunit;

namespace SpaceInspector.IntegrationTests;

public sealed class DuplicateTests : IDisposable
{
    private readonly string _area = Path.Combine(Path.GetTempPath(), "SpaceInspector.Tests", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_area, "files");
    private readonly WindowsVolumeIdentityService _volumes = new();
    public DuplicateTests() => Directory.CreateDirectory(Root);
    private string Write(string name, byte[]? data = null)
    {
        var path = Path.Combine(Root, name); File.WriteAllBytes(path, data ?? [1, 2, 3]); return path;
    }
    private async Task<SqliteIndexDatabase> Index()
    {
        var db = new SqliteIndexDatabase(Path.Combine(_area, "index.db"), _volumes);
        await db.InitializeAsync();
        await new FileIndexingService(new MetadataOnlyFileSource(volumes: _volumes), db).ScanAsync([Root]);
        return db;
    }
    private DuplicateAnalysisService Analyzer(SqliteIndexDatabase db) => new(db, new WindowsFileFingerprintService(_volumes));

    [Fact]
    public async Task FullHashConfirmsContentRegardlessOfNameAndHandlesEmptyFiles()
    {
        Write("a.txt"); Write("renamed.bin"); Write("different.txt", [9, 8, 7]); Write("empty-a", []); Write("empty-b", []);
        var db = await Index();
        var result = await Analyzer(db).AnalyzeAsync(new(IncludeProtected: true), null, default);
        Assert.Equal("Completed", result.Status); Assert.Equal(0, result.Unverified);
        var groups = await db.GetDuplicateGroupsAsync(0, 100);
        Assert.Equal(2, groups.Count); Assert.All(groups, g => Assert.Equal("ConfirmedDuplicate", g.Status));
        Assert.Equal(3, groups.Sum(g => g.LogicalDuplicateBytes));
        Assert.All(groups, g => Assert.Equal(2, g.DataCount));
        var members = await db.GetDuplicateMembersAsync(groups.Single(g => g.LogicalDuplicateBytes == 3).GroupId, 0, 100);
        Assert.Contains(members, f => f.FullPath.EndsWith("a.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(groups.SelectMany(g => db.GetDuplicateMembersAsync(g.GroupId, 0, 100).GetAwaiter().GetResult()), f => f.FullPath.EndsWith("different.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartialHashCollisionIsOnlyCandidateUntilFullVerification()
    {
        var a = new byte[200000]; var b = new byte[200000]; b[100000] = 1;
        Write("a", a); Write("b", b); var db = await Index();
        await Analyzer(db).AnalyzeAsync(new(DuplicateLevel.PartialHash, true), null, default);
        Assert.Equal("StrongCandidate", Assert.Single(await db.GetDuplicateGroupsAsync(0, 100)).Status);
        await Analyzer(db).AnalyzeAsync(new(DuplicateLevel.FullHash, true), null, default);
        Assert.Empty(await db.GetDuplicateGroupsAsync(0, 100));
    }

    [Fact]
    public async Task SizeLevelDoesNotClaimConfirmedDuplicates()
    {
        Write("a"); Write("b", [9, 8, 7]); var db = await Index();
        await Analyzer(db).AnalyzeAsync(new(DuplicateLevel.Size, true), null, default);
        Assert.Equal("SuspectedDuplicate", Assert.Single(await db.GetDuplicateGroupsAsync(0, 100)).Status);
    }
    [Fact]
    public async Task CandidateAndMemberPagingCrossesBatchBoundaries()
    {
        for (var i = 0; i < 105; i++) Write($"{i:D3}.bin", []);
        var db = await Index();
        await Analyzer(db).AnalyzeAsync(new(IncludeProtected: true), null, default);
        var group = Assert.Single(await db.GetDuplicateGroupsAsync(0, 100));
        Assert.Equal(105, group.FileCount);
        var first = await db.GetDuplicateMembersAsync(group.GroupId, 0, 100);
        var next = await db.GetDuplicateMembersAsync(group.GroupId, 1, 100);
        Assert.Equal(100, first.Count); Assert.Equal(5, next.Count);
        Assert.Equal(105, first.Concat(next).Select(f => f.FileId).Distinct().Count());
    }

    [Fact]
    public async Task LockedAndChangedFilesRemainUnverified()
    {
        var changed = Write("changed"); var lockedPath = Write("locked"); var db = await Index();
        File.WriteAllBytes(changed, [8, 8, 8, 8]);
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = await Analyzer(db).AnalyzeAsync(new(IncludeProtected: true), null, default);
        Assert.Equal(2, result.Unverified);
        var group = Assert.Single(await db.GetDuplicateGroupsAsync(0, 100));
        Assert.Equal("Unverified", group.Status);
        Assert.All(await db.GetDuplicateMembersAsync(group.GroupId, 0, 100), f => Assert.NotNull(f.Error));
    }

    [Fact]
    public async Task DefaultPolicySkipsSystemFilesAndUnknownDeviceNeverOpensContent()
    {
        var a = Write("a"); var b = Write("b");
        File.SetAttributes(a, File.GetAttributes(a) | FileAttributes.System);
        File.SetAttributes(b, File.GetAttributes(b) | FileAttributes.System);
        Assert.True(WindowsFileFingerprintService.ProtectedPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "fixture.bin")));
        var db = await Index();
        var result = await Analyzer(db).AnalyzeAsync(new(), null, default);
        Assert.Equal(2, result.Unverified);
        var path = Path.Combine(Root, "a");
        var file = new FileInfo(path);
        var fingerprint = await new WindowsFileFingerprintService(_volumes).ComputeAsync(
            new(1, path, file.Length, file.LastWriteTimeUtc.ToString("O"), "wrong-device"), DuplicateLevel.FullHash, true, default);
        Assert.Equal("Unverified", fingerprint.Status);
        Assert.Null(fingerprint.Hash);
    }

    [Fact]
    public async Task ChangedAfterHashIsNotPublishedAsConfirmed()
    {
        Write("a"); Write("b"); var db = await Index();
        var fake = new FakeFingerprints { Current = false };
        await new DuplicateAnalysisService(db, fake).AnalyzeAsync(new(), null, default);
        Assert.DoesNotContain(await db.GetDuplicateGroupsAsync(0, 100), g => g.Status == "ConfirmedDuplicate");
        Assert.All(await db.GetDuplicateMembersAsync("unverified", 0, 100), f => Assert.NotNull(f.Error));
    }
    [Fact]
    public async Task OfflineAttributePreventsContentHashing()
    {
        var path = Write("offline.bin");
        var original = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, original | FileAttributes.Offline);
            Assert.True(MetadataOnlyFileSource.IsCloudOnly(File.GetAttributes(path)));
            var info = new FileInfo(path);
            var result = await new WindowsFileFingerprintService(_volumes).ComputeAsync(
                new(1, path, info.Length, info.LastWriteTimeUtc.ToString("O"), _volumes.GetIdentity(path)), DuplicateLevel.FullHash, true, default);
            Assert.Equal("Unverified", result.Status); Assert.Null(result.Hash);
        }
        finally { File.SetAttributes(path, original); }
    }

    [Fact]
    public async Task CancelledAnalysisDoesNotPublishConfirmedGroupsAndCanRetry()
    {
        Write("a"); Write("b"); var db = await Index();
        var fake = new FakeFingerprints { Block = true };
        using var cancellation = new CancellationTokenSource();
        var task = new DuplicateAnalysisService(db, fake).AnalyzeAsync(new(), null, cancellation.Token);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        Assert.Equal("Cancelled", (await task.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.DoesNotContain(await db.GetDuplicateGroupsAsync(0, 100), g => g.Status == "ConfirmedDuplicate");
        Assert.Equal("Completed", (await Analyzer(db).AnalyzeAsync(new(IncludeProtected: true), null, default)).Status);
    }

    [JunctionFact]
    public async Task HardlinksDoNotCountAsAdditionalDuplicateSpace()
    {
        var original = Write("original"); var alias = Path.Combine(Root, "hardlink");
        Assert.True(CreateHardLink(alias, original, IntPtr.Zero), "Failed to create hardlink: " + Marshal.GetLastWin32Error());
        var db = await Index();
        await Analyzer(db).AnalyzeAsync(new(IncludeProtected: true), null, default);
        var group = Assert.Single(await db.GetDuplicateGroupsAsync(0, 100));
        Assert.Equal("ConfirmedDuplicate", group.Status); Assert.Equal(2, group.FileCount); Assert.Equal(1, group.DataCount);
        Assert.Equal(0, group.LogicalDuplicateBytes);
        Write("independent"); await new FileIndexingService(new MetadataOnlyFileSource(volumes: _volumes), db).ScanAsync([Root]);
        await Analyzer(db).AnalyzeAsync(new(IncludeProtected: true), null, default);
        group = Assert.Single(await db.GetDuplicateGroupsAsync(0, 100));
        Assert.Equal(3, group.FileCount); Assert.Equal(2, group.DataCount); Assert.Equal(3, group.LogicalDuplicateBytes);
    }

    private sealed class FakeFingerprints : IFileFingerprintService
    {
        public bool Current { get; init; } = true;
        public bool Block { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FileFingerprint> ComputeAsync(DuplicateCandidate file, DuplicateLevel level, bool includeProtected, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(); if (Block) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new("Verified", "HASH", "physical-" + file.FileId);
        }
        public Task<bool> IsCurrentAsync(DuplicateCandidate file, CancellationToken cancellationToken) => Task.FromResult(Current);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string newName, string existing, IntPtr security);
    public void Dispose() { if (Directory.Exists(_area)) Directory.Delete(_area, true); }
}
