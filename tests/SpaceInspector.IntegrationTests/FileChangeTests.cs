using System.Text.Json;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.FileSystem;
using Xunit;
namespace SpaceInspector.IntegrationTests;

public sealed class FileChangeTests : IDisposable
{
    private readonly string _area = Path.Combine(Path.GetTempPath(), "SI-actions-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsVolumeIdentityService _volumes = new();
    private string Root => Path.Combine(_area, "files");
    private async Task<(WorkSession Session, IndexedFile File)> Seed()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllBytes(Path.Combine(Root, "a.jpg"), [1, 2, 3]); File.WriteAllBytes(Path.Combine(Root, "b.jpg"), [1, 2, 3]);
        var spec = new WorkSpecification { Mode = TargetMode.Folders, Roots = [Root], Extensions = ["jpg"], Recursive = true,
            Volumes = new() { [Path.GetPathRoot(Root)!] = _volumes.GetIdentity(Root) } }.Normalize();
        var session = new WorkSession(Path.Combine(_area, "jobs", "job"), new() { Specification = spec }, _volumes);
        await session.ExecuteAsync(true, null, default);
        var file = (await session.Result!.SearchAsync(new() { Name = "a.jpg" }, 0, 100)).Files.Single();
        return (session, file);
    }
    [Fact]
    public async Task RenamePreservesDataDuplicateIdentityAndKeepDesignation()
    {
        var (session, file) = await Seed(); var target = Path.Combine(Root, "renamed.jpg");
        File.WriteAllText(Path.Combine(session.DirectoryPath, "kept-files.json"), JsonSerializer.Serialize(new[] { file.FullPath }));
        var actions = new WorkFileActions(session, _volumes); var plan = await actions.PrepareAsync(file, target);
        Assert.True(plan.RemainsInScope); await actions.ExecuteAsync(plan);
        Assert.False(File.Exists(file.FullPath)); Assert.Equal(new byte[] {1, 2, 3}, File.ReadAllBytes(target));
        var changed = (await session.Result!.SearchAsync(new() { Name = "renamed" }, 0, 100)).Files.Single();
        Assert.Equal(file.FileId, changed.FileId); Assert.Equal(file.PhysicalId, changed.PhysicalId);
        Assert.Equal("ConfirmedDuplicate", changed.DuplicateStatus); Assert.Equal(2, changed.GroupDataCount);
        Assert.Single((await session.Index.SearchAsync(new() { Name = "renamed" }, 0, 100)).Files);
        Assert.Equal(new[] {target}, JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(session.DirectoryPath, "kept-files.json"))));
        Assert.Equal(0, await actions.RecoverAsync());
    }
    [Fact]
    public async Task MoveOutsideScopeUpdatesBothIndexesWithoutExpandingWork()
    {
        var (session, file) = await Seed(); var folder = Path.Combine(_area, "archive"); Directory.CreateDirectory(folder);
        var actions = new WorkFileActions(session, _volumes); var plan = await actions.PrepareAsync(file, Path.Combine(folder, file.FileName));
        Assert.False(plan.RemainsInScope); await actions.ExecuteAsync(plan);
        var moved = (await session.Result!.SearchAsync(new() { Name = "a.jpg" }, 0, 100)).Files.Single();
        Assert.Equal("MovedOutsideScope", moved.ScanStatus); Assert.Null(moved.DuplicateGroupId);
        Assert.Equal("NotDuplicate", (await session.Result.SearchAsync(new() { Name = "b.jpg" }, 0, 100)).Files.Single().DuplicateStatus);
        Assert.False(session.Record.Specification.AllowsFile(plan.TargetPath));
    }
    [Fact]
    public async Task ChangedFileAndDestinationCollisionAreRejectedWithoutMoving()
    {
        var (session, file) = await Seed(); var target = Path.Combine(Root, "collision.jpg");
        var actions = new WorkFileActions(session, _volumes); var plan = await actions.PrepareAsync(file, target);
        File.WriteAllBytes(target, [9]);
        await Assert.ThrowsAnyAsync<IOException>(() => actions.ExecuteAsync(plan));
        Assert.Equal(new byte[] {9}, File.ReadAllBytes(target)); Assert.True(File.Exists(file.FullPath)); File.Delete(target);
        File.AppendAllText(file.FullPath, "changed");
        await Assert.ThrowsAnyAsync<IOException>(() => actions.ExecuteAsync(plan)); Assert.False(File.Exists(target));
    }
    [Fact]
    public async Task RecoveryReconcilesInterruptedMoveWithoutMovingAgain()
    {
        var (session, file) = await Seed(); var target = Path.Combine(Root, "recovered.jpg");
        File.WriteAllText(Path.Combine(session.DirectoryPath, "kept-files.json"), JsonSerializer.Serialize(new[] {file.FullPath}));
        var actions = new WorkFileActions(session, _volumes); var plan = await actions.PrepareAsync(file, target);
        WindowsFileChange.Move(plan.Source, target, _volumes, session.DirectoryPath, () => WritePending(session, plan));
        await session.Index.ApplyFileChangeAsync(plan); // Interrupted after only the first DB update.
        Assert.Equal(1, await actions.RecoverAsync()); Assert.Equal(0, await actions.RecoverAsync());
        Assert.Single((await session.Result!.SearchAsync(new() { Name = "recovered" }, 0, 100)).Files);
        Assert.Equal(new[] {target}, JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(session.DirectoryPath, "kept-files.json"))));
    }
    [Fact]
    public async Task RecoveryNeverExecutesAnUnstartedMove()
    {
        var (session, file) = await Seed(); var target = Path.Combine(Root, "unstarted.jpg");
        var actions = new WorkFileActions(session, _volumes); var plan = await actions.PrepareAsync(file, target); WritePending(session, plan);
        Assert.Equal(0, await actions.RecoverAsync()); Assert.True(File.Exists(file.FullPath)); Assert.False(File.Exists(target));
    }
    [Fact]
    public async Task DestinationAppearingAtRenameDoesNotOverwriteOrBlockRecovery()
    {
        var (session, file) = await Seed(); var target = Path.Combine(Root, "raced.jpg");
        var actions = new WorkFileActions(session, _volumes); var plan = await actions.PrepareAsync(file, target);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => WindowsFileChange.Move(plan.Source, target, _volumes, session.DirectoryPath, () => {
            WritePending(session, plan); File.WriteAllBytes(target, [9]);
        }));
        Assert.Equal(new byte[] {9}, File.ReadAllBytes(target)); Assert.True(File.Exists(file.FullPath));
        Assert.Equal(0, await actions.RecoverAsync());
    }
    [Fact]
    public async Task RenamedExtensionOutsideScopeIsNotMarkedMissingByRescan()
    {
        var (session, file) = await Seed(); var target = Path.Combine(Root, "outside.png");
        var actions = new WorkFileActions(session, _volumes); var plan = await actions.PrepareAsync(file, target);
        await actions.ExecuteAsync(plan);
        var scanner = new SpaceInspector.Core.Indexing.FileIndexingService(
            new MetadataOnlyFileSource(volumes: _volumes, specification: session.Record.Specification), session.Index);
        await scanner.ScanAsync([Root], null, default);
        Assert.True(File.Exists(target));
        Assert.Equal("MovedOutsideScope", (await session.Index.SearchAsync(new() { Name = "outside" }, 0, 100)).Files.Single().ScanStatus);
    }
    [Theory]
    [InlineData("CON.jpg")]
    [InlineData("bad:stream.jpg")]
    [InlineData("trailing.jpg.")]
    [InlineData("trailing.jpg ")]
    public async Task InvalidNamesLeaveTheSourceUntouched(string name)
    {
        var (session, file) = await Seed();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new WorkFileActions(session, _volumes).PrepareAsync(file, Path.Combine(Root, name)));
        Assert.True(File.Exists(file.FullPath));
    }
    [Fact]
    public async Task DeviceMismatchAndReadOnlySourceAreRejected()
    {
        var (session, file) = await Seed(); var target = Path.Combine(Root, "target.jpg");
        await Assert.ThrowsAnyAsync<IOException>(() => new WorkFileActions(session, new OtherTargetVolume(_volumes, target)).PrepareAsync(file, target));
        File.SetAttributes(file.FullPath, FileAttributes.ReadOnly);
        try { await Assert.ThrowsAnyAsync<IOException>(() => new WorkFileActions(session, _volumes).PrepareAsync(file, target)); }
        finally { File.SetAttributes(file.FullPath, FileAttributes.Normal); }
        Assert.True(File.Exists(file.FullPath)); Assert.False(File.Exists(target));
    }
    [Fact]
    public async Task BatchPreflightRejectsDuplicateTargetsBeforeAnyMutation()
    {
        var (session, _) = await Seed(); var files = (await session.Result!.SearchAsync(new(), 0, 100)).Files;
        var actions = new WorkFileActions(session, _volumes);
        await Assert.ThrowsAsync<IOException>(() => actions.PrepareBatchAsync(files.Select(f => new FileChangeRequest(f, Path.Combine(Root, "same.jpg"))).ToArray()));
        Assert.All(files, file => Assert.True(File.Exists(file.FullPath)));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchStopsAfterRaceOrCancellationAndKeepsCompletedChanges(bool cancel)
    {
        var (session, _) = await Seed(); var files = (await session.Result!.SearchAsync(new(), 0, 100)).Files.OrderBy(f => f.FileName).ToArray();
        var actions = new WorkFileActions(session, _volumes);
        var plans = await actions.PrepareBatchAsync(files.Select(f => new FileChangeRequest(f, Path.Combine(Root, "new-" + f.FileName))).ToArray());
        using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => {
            if (!p.Stage.StartsWith("2/2")) return;
            if (cancel) cts.Cancel(); else File.WriteAllBytes(plans[1].TargetPath, [9]);
        });
        var results = await actions.ExecuteBatchAsync(plans, cts.Token, progress);
        Assert.Equal("Completed", results[0].State); Assert.Equal(cancel ? "Cancelled" : "Failed", results[1].State);
        Assert.False(File.Exists(files[0].FullPath)); Assert.True(File.Exists(files[1].FullPath));
        Assert.Equal(new byte[] {1, 2, 3}, File.ReadAllBytes(plans[0].TargetPath));
        if (!cancel) Assert.Equal(new byte[] {9}, File.ReadAllBytes(plans[1].TargetPath));
        Assert.Single(Directory.GetFiles(Path.Combine(session.DirectoryPath, "file-actions", "reports"), "*.json"));
    }
    private sealed class InlineProgress(Action<FileChangeProgress> action) : IProgress<FileChangeProgress>
    { public void Report(FileChangeProgress value) => action(value); }
    [Fact]
    public async Task UndoOutsideScopeRestoresPathAndKeepButRequiresNewDuplicateAnalysis()
    {
        var (session, file) = await Seed(); var folder = Path.Combine(_area, "archive"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(session.DirectoryPath, "kept-files.json"), JsonSerializer.Serialize(new[] {file.FullPath}));
        var actions = new WorkFileActions(session, _volumes);
        await actions.ExecuteAsync(await actions.PrepareAsync(file, Path.Combine(folder, "moved.jpg")));
        var moved = (await session.Result!.SearchAsync(new() { Name = "moved" }, 0, 100)).Files.Single();
        var undo = await actions.PrepareUndoAsync(moved); Assert.NotNull(undo.UndoOf);
        await actions.ExecuteAsync(undo);
        Assert.True(File.Exists(file.FullPath)); Assert.False(File.Exists(moved.FullPath));
        var restored = (await session.Result.SearchAsync(new() { Name = "a.jpg" }, 0, 100)).Files.Single();
        Assert.Equal("Normal", restored.ScanStatus); Assert.Null(restored.DuplicateGroupId); Assert.Equal("Unverified", restored.DuplicateStatus);
        Assert.Contains((await session.Result.SearchAsync(new() {DuplicateStatuses = ["Unverified"]}, 0, 100)).Files, f => f.FileId == file.FileId);
        Assert.Equal(new[] {file.FullPath}, JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(session.DirectoryPath, "kept-files.json"))));
        await Assert.ThrowsAsync<IOException>(() => actions.PrepareUndoAsync(restored));
        Assert.Equal(0, await actions.RecoverAsync());
    }
    [Fact]
    public async Task UndoRejectsOccupiedOriginalAndModifiedDestination()
    {
        var (session, file) = await Seed(); var target = Path.Combine(Root, "moved.jpg");
        var actions = new WorkFileActions(session, _volumes); await actions.ExecuteAsync(await actions.PrepareAsync(file, target));
        var moved = (await session.Result!.SearchAsync(new() {Name = "moved"}, 0, 100)).Files.Single();
        File.WriteAllBytes(file.FullPath, [9]);
        await Assert.ThrowsAsync<IOException>(() => actions.PrepareUndoAsync(moved));
        Assert.Equal(new byte[] {9}, File.ReadAllBytes(file.FullPath)); File.Delete(file.FullPath);
        File.AppendAllText(target, "changed");
        await Assert.ThrowsAsync<IOException>(() => actions.PrepareUndoAsync(moved)); Assert.False(File.Exists(file.FullPath));
    }
    [Fact]
    public async Task InterruptedUndoRecoversWithoutMovingAgain()
    {
        var (session, file) = await Seed(); var target = Path.Combine(Root, "moved.jpg");
        var actions = new WorkFileActions(session, _volumes); await actions.ExecuteAsync(await actions.PrepareAsync(file, target));
        var moved = (await session.Result!.SearchAsync(new() {Name = "moved"}, 0, 100)).Files.Single();
        var undo = await actions.PrepareUndoAsync(moved);
        WindowsFileChange.Move(undo.Source, undo.TargetPath, _volumes, session.DirectoryPath, () => WritePending(session, undo));
        await session.Index.ApplyFileChangeAsync(undo);
        Assert.Equal(1, await actions.RecoverAsync()); Assert.Equal(0, await actions.RecoverAsync());
        Assert.True(File.Exists(file.FullPath)); Assert.False(File.Exists(target));
    }
    [Fact]
    public async Task BatchUndoRestoresOutsideScopeFilesAndKeepDesignation()
    {
        var (session, first) = await Seed(); var files = (await session.Result!.SearchAsync(new(), 0, 100)).Files;
        var archive = Path.Combine(_area, "archive"); Directory.CreateDirectory(archive);
        File.WriteAllText(Path.Combine(session.DirectoryPath, "kept-files.json"), JsonSerializer.Serialize(new[] { first.FullPath }));
        var actions = new WorkFileActions(session, _volumes);
        var forward = await actions.PrepareBatchAsync(files.Select(f => new FileChangeRequest(f, Path.Combine(archive, f.FileName))).ToArray());
        Assert.All(await actions.ExecuteBatchAsync(forward), o => Assert.Equal("Completed", o.State));
        var moved = (await session.Result.SearchAsync(new(), 0, 100)).Files;
        Assert.All(moved, f => Assert.Equal("MovedOutsideScope", f.ScanStatus));
        var undo = await actions.PrepareUndoBatchAsync(moved);
        Assert.All(await actions.ExecuteBatchAsync(undo), o => Assert.Equal("Completed", o.State));
        Assert.All(files, f => Assert.True(File.Exists(f.FullPath))); Assert.All(moved, f => Assert.False(File.Exists(f.FullPath)));
        Assert.All((await session.Result.SearchAsync(new(), 0, 100)).Files, f => Assert.Equal("Unverified", f.DuplicateStatus));
        Assert.Equal(new[] { first.FullPath }, JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(session.DirectoryPath, "kept-files.json"))));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchUndoPreflightFailureLeavesEveryFileUnchanged(bool missingHistory)
    {
        var (session, first) = await Seed(); var files = (await session.Result!.SearchAsync(new(), 0, 100)).Files;
        var actions = new WorkFileActions(session, _volumes);
        var requests = (missingHistory ? files.Take(1) : files).Select(f => new FileChangeRequest(f, Path.Combine(Root, "new-" + f.FileName))).ToArray();
        await actions.ExecuteBatchAsync(await actions.PrepareBatchAsync(requests));
        var current = (await session.Result.SearchAsync(new(), 0, 100)).Files;
        if (!missingHistory) File.WriteAllBytes(first.FullPath, [9]);
        await Assert.ThrowsAsync<IOException>(() => actions.PrepareUndoBatchAsync(current));
        Assert.All(current, f => Assert.True(File.Exists(f.FullPath)));
        if (!missingHistory) Assert.Equal(new byte[] {9}, File.ReadAllBytes(first.FullPath));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchUndoStopsAfterCompletedFileAndCanRetryRemaining(bool cancel)
    {
        var (session, _) = await Seed(); File.WriteAllBytes(Path.Combine(Root, "c.jpg"), [1, 2, 3]);
        await session.ExecuteAsync(true, null, default);
        var files = (await session.Result!.SearchAsync(new(), 0, 100)).Files;
        var actions = new WorkFileActions(session, _volumes);
        await actions.ExecuteBatchAsync(await actions.PrepareBatchAsync(files.Select(f => new FileChangeRequest(f, Path.Combine(Root, "new-" + f.FileName))).ToArray()));
        var undo = await actions.PrepareUndoBatchAsync((await session.Result.SearchAsync(new(), 0, 100)).Files);
        using var cts = new CancellationTokenSource();
        var outcomes = await actions.ExecuteBatchAsync(undo, cts.Token, new InlineProgress(p => {
            if (!p.Stage.StartsWith("2/3")) return;
            if (cancel) cts.Cancel(); else File.WriteAllBytes(undo[1].TargetPath, [9]);
        }));
        Assert.Equal(new[] { "Completed", cancel ? "Cancelled" : "Failed", "NotProcessed" }, outcomes.Select(o => o.State));
        Assert.True(File.Exists(undo[0].TargetPath)); Assert.True(File.Exists(undo[1].Source.FullPath)); Assert.True(File.Exists(undo[2].Source.FullPath));
        if (!cancel) { Assert.Equal(new byte[] {9}, File.ReadAllBytes(undo[1].TargetPath)); File.Delete(undo[1].TargetPath); }
        var remaining = (await session.Result.SearchAsync(new() {Name = "new-"}, 0, 100)).Files;
        Assert.Equal(2, remaining.Count);
        Assert.All(await actions.ExecuteBatchAsync(await actions.PrepareUndoBatchAsync(remaining)), o => Assert.Equal("Completed", o.State));
        Assert.All(files, f => Assert.True(File.Exists(f.FullPath)));
    }
    private sealed class OtherTargetVolume(IVolumeIdentityService inner, string target) : IVolumeIdentityService
    { public string? GetIdentity(string path) => path == target ? "other-volume" : inner.GetIdentity(path); }
    private static void WritePending(WorkSession session, FileChangePlan plan) => File.WriteAllText(
        Path.Combine(session.DirectoryPath, "file-actions", plan.Id.ToString("N") + ".json"), JsonSerializer.Serialize(new FileChangeRecord(plan, "Pending", DateTimeOffset.UtcNow)));
    public void Dispose() { if (Directory.Exists(_area)) Directory.Delete(_area, true); }
}
