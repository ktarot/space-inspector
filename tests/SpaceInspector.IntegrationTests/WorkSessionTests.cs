using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.FileSystem;
using Xunit;

namespace SpaceInspector.IntegrationTests;

public sealed class WorkSessionTests : IDisposable
{
    private readonly string _area = Path.Combine(Path.GetTempPath(), "SpaceInspector.Tests", Guid.NewGuid().ToString("N"));
    private readonly WindowsVolumeIdentityService _volumes = new();
    private string Root => Path.Combine(_area, "files");
    public WorkSessionTests() => Directory.CreateDirectory(Root);
    private string Write(string relative, byte[]? bytes = null)
    {
        var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? [1, 2, 3]); return path;
    }
    private WorkSpecification Spec(bool recursive = true, params string[] roots) => new WorkSpecification
    {
        Mode = TargetMode.Folders, Roots = roots.Length == 0 ? [Root] : roots, Recursive = recursive, Extensions = ["jpg"],
        Volumes = new() { [Path.GetPathRoot(Root)!] = _volumes.GetIdentity(Root) }
    }.Normalize();
    private WorkSession Session(WorkSpecification spec, Func<WorkSpecification, IFileMetadataSource>? source = null, IFileFingerprintService? fingerprints = null)
        => new(Path.Combine(_area, "jobs", Guid.NewGuid().ToString("N")), new() { Specification = spec }, _volumes, fingerprints, source);

    [Fact]
    public async Task ScopeDepthAndExtensionsApplyToIndexAndDuplicateResults()
    {
        Write("a.jpg"); Write("b.JPG"); Write("ignored.png"); Write("child/c.jpg");
        var session = Session(Spec(false)); await session.ExecuteAsync(true, null, default);
        Assert.Equal(2, (await session.Index.GetPageAsync(0, 100)).TotalCount);
        var group = Assert.Single(await session.Result!.GetDuplicateGroupsAsync(0, 100));
        Assert.Equal(2, group.FileCount); Assert.Equal("ConfirmedDuplicate", group.Status);
        var recursive = Session(Spec()); await recursive.ExecuteAsync(true, null, default);
        Assert.Equal(3, (await recursive.Index.GetPageAsync(0, 100)).TotalCount);
        Assert.Equal(3, Assert.Single(await recursive.Result!.GetDuplicateGroupsAsync(0, 100)).FileCount);
    }

    [Fact]
    public async Task ExplicitChildIsPreservedWhenRecursionIsOff()
    {
        Write("a.jpg"); Write("child/b.jpg"); Write("child/deeper/c.jpg");
        var session = Session(Spec(false, Root, Path.Combine(Root, "child")));
        await session.ExecuteAsync(true, null, default);
        Assert.Equal(2, (await session.Index.GetPageAsync(0, 100)).TotalCount);
    }

    [Fact]
    public void DriveModeRejectsFoldersAndProtectedSystemFilesAreNotEligible()
    {
        Assert.Throws<ArgumentException>(() => (Spec() with { Mode = TargetMode.Drives }).Normalize());
        Assert.False((Spec() with { Extensions = [".sys"] }).AllowsFile(Path.Combine(Root, "pagefile.sys")));
        Assert.False(Spec().AllowsFile(Root + "other/a.jpg"));
        Assert.False(Spec().AllowsFile(Path.Combine(Root, "..", "outside.jpg")));
        Assert.False(Spec().AllowsDirectory(Path.Combine(Root, "..", "outside")));
        Assert.Throws<ArgumentException>(() => (Spec() with { Extensions = ["*"] }).Normalize());
    }

    [Fact]
    public async Task WaitingForPermissionDoesNotStopOtherFilesAndRetryKeepsOriginalResult()
    {
        Write("a.jpg"); Write("b.jpg"); var denied = Path.Combine(Root, "restricted");
        var deny = true;
        var session = Session(Spec(), s => new SyntheticSource(new MetadataOnlyFileSource(volumes: _volumes, specification: s),
            () => deny ? new(denied, "AccessDenied", "permission required") : null));
        await session.ExecuteAsync(true, null, default);
        Assert.Contains("응답 대기", session.Record.Status);
        Assert.Equal(2, Assert.Single(await session.Result!.GetDuplicateGroupsAsync(0, 100)).FileCount);
        var previous = session.Record.ResultFile;
        Write("restricted/c.jpg"); deny = false;
        session.Decide(denied, true); await session.ExecuteAsync(false, null, default);
        Assert.Equal("재시도 완료", Assert.Single(session.Pending).State);
        Assert.Equal(3, Assert.Single(await session.Result!.GetDuplicateGroupsAsync(0, 100)).FileCount);
        Assert.NotEqual(previous, session.Record.ResultFile);
        Assert.True(File.Exists(Path.Combine(session.DirectoryPath, previous!)));
    }

    [Fact]
    public async Task CancellationRetainsAlreadyVerifiedGroupAndUnprocessedCandidates()
    {
        Write("a.jpg"); Write("b.jpg"); Write("c.jpg"); Write("d.jpg");
        var hashes = new BlockingFingerprints(); var session = Session(Spec(), fingerprints: hashes);
        using var cancel = new CancellationTokenSource();
        var run = session.ExecuteAsync(true, null, cancel.Token);
        await hashes.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("중단", session.Record.Status);
        var groups = await session.Result!.GetDuplicateGroupsAsync(0, 100);
        Assert.Equal(2, Assert.Single(groups, g => g.Status == "ConfirmedDuplicate").FileCount);
        Assert.Equal(2, Assert.Single(groups, g => g.Status == "StrongCandidate").FileCount);
        Assert.Equal(4, (await session.Index.GetPageAsync(0, 100)).TotalCount);
    }

    [Fact]
    public async Task DeviceReplacementIsPendingRatherThanReadingDifferentDisk()
    {
        Write("a.jpg"); var spec = Spec(); spec.Volumes[Path.GetPathRoot(Root)!] = "different-volume";
        var session = Session(spec); await session.ExecuteAsync(true, null, default);
        Assert.Equal(0, (await session.Index.GetPageAsync(0, 100)).TotalCount);
        Assert.Equal("응답 대기", Assert.Single(session.Pending).State);
    }

    [Fact]
    public async Task PermissionWorkerProtocolRejectsOutOfScopePathsWithoutElevation()
    {
        Write("a.jpg"); var channel = Path.Combine(_area, "channel"); Directory.CreateDirectory(channel);
        await File.WriteAllTextAsync(Path.Combine(channel, "scope.json"), System.Text.Json.JsonSerializer.Serialize(Spec()));
        var request = new PermissionRequest("scan", [Path.GetTempPath()]);
        await File.WriteAllTextAsync(Path.Combine(channel, "test.request"), System.Text.Json.JsonSerializer.Serialize(request));
        var worker = PermissionWorker.RunAsync(channel, Environment.ProcessId);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(Path.Combine(channel, "test.reply"))) await Task.Delay(50, timeout.Token);
            var reply = System.Text.Json.JsonSerializer.Deserialize<PermissionReply>(await File.ReadAllTextAsync(Path.Combine(channel, "test.reply")));
            Assert.Contains("범위 밖", reply!.Error);
        }
        finally { File.WriteAllText(Path.Combine(channel, "stop"), ""); await worker; }
    }

    [Fact]
    public async Task ReplyDuringScanSchedulesRetryWithoutBlockingOtherFiles()
    {
        Write("a.jpg"); Write("b.jpg"); var restricted = Path.Combine(Root, "restricted");
        var deny = true; WorkSession? session = null;
        session = Session(Spec(), s => new SyntheticSource(new MetadataOnlyFileSource(volumes: _volumes, specification: s),
            () => deny ? new(restricted, "AccessDenied", "permission required") : null));
        var progress = new InlineProgress<WorkUpdate>(p =>
        {
            if (p.Path != restricted || !deny) return;
            deny = false; Write("restricted/c.jpg"); session.Decide(restricted, true);
        });
        await session.ExecuteAsync(true, progress, default);
        Assert.Equal(3, (await session.Index.GetPageAsync(0, 100)).TotalCount);
        Assert.Equal("재시도 완료", Assert.Single(session.Pending).State);
    }

    [Fact]
    public async Task NonrecursiveRetryDoesNotMarkExplicitChildMissing()
    {
        Write("a.jpg"); Write("child/b.jpg");
        var denied = true;
        var session = Session(Spec(false, Root, Path.Combine(Root, "child")), s => new SyntheticSource(
            new MetadataOnlyFileSource(volumes: _volumes, specification: s), () => denied ? new(Path.Combine(Root, "a.jpg"), "AccessDenied", "retry") : null));
        await session.ExecuteAsync(true, null, default); denied = false;
        session.Decide(Path.Combine(Root, "a.jpg"), true); await session.ExecuteAsync(false, null, default);
        Assert.All((await session.Index.GetPageAsync(0, 100)).Files, f => Assert.Equal("Normal", f.ScanStatus));
    }

    [Fact]
    public async Task InterruptedResultRecoversPersistedVerifiedHashes()
    {
        Write("a.jpg"); Write("b.jpg");
        var session = Session(Spec()); await session.ExecuteAsync(true, null, default);
        var db = session.Result!; var run = await db.BeginDuplicatesAsync(default);
        var candidates = await db.GetDuplicateCandidatesAsync(0, 100, false, default);
        foreach (var file in candidates)
        {
            await db.StoreFingerprintAsync(run, file, new("Verified", "partial", "physical-" + file.FileId), false, default);
            await db.StoreFingerprintAsync(run, file, new("Verified", "full", "physical-" + file.FileId), true, default);
        }
        await db.RecoverWorkResultAsync();
        Assert.Equal("ConfirmedDuplicate", Assert.Single(await db.GetDuplicateGroupsAsync(0, 100)).Status);
    }

    [Fact]
    public async Task RecycledFileIsRemovedFromCurrentDuplicateResultAndIndex()
    {
        var first = Write("a.jpg"); Write("b.jpg");
        var session = Session(Spec()); await session.ExecuteAsync(true, null, default);
        var result = session.Result!;
        var group = Assert.Single(await result.GetDuplicateGroupsAsync(0, 100));
        var member = (await result.GetDuplicateMembersAsync(group.GroupId, 0, 100)).Single(m => m.FullPath == first);
        await result.MarkRecycledAsync(member.FileId, member.FullPath);
        await session.Index.MarkRecycledAsync(member.FileId, member.FullPath);
        Assert.Empty(await result.GetDuplicateGroupsAsync(0, 100));
        Assert.Equal(1, (await result.GetPageAsync(0, 100)).Files.Count(f => f.ScanStatus == "Normal"));
        Assert.Equal(1, (await session.Index.GetPageAsync(0, 100)).Files.Count(f => f.ScanStatus == "Normal"));
    }

    private sealed class SyntheticSource(IFileMetadataSource inner, Func<ScanIssue?> issue) : IFileMetadataSource
    {
        public IEnumerable<ScanObservation> Enumerate(IReadOnlyList<string> roots, CancellationToken token)
        {
            if (issue() is { } problem) yield return new(null, problem);
            foreach (var item in inner.Enumerate(roots, token)) yield return item;
        }
    }
    private sealed class BlockingFingerprints : IFileFingerprintService
    {
        private int _full;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<FileFingerprint> ComputeAsync(DuplicateCandidate file, DuplicateLevel level, bool includeProtected, CancellationToken token)
        {
            if (level == DuplicateLevel.FullHash && ++_full == 3) { Blocked.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new("Verified", "same", "physical-" + file.FileId);
        }
        public Task<bool> IsCurrentAsync(DuplicateCandidate file, CancellationToken token) => Task.FromResult(true);
    }
    public void Dispose() { if (Directory.Exists(_area)) Directory.Delete(_area, true); }
}
