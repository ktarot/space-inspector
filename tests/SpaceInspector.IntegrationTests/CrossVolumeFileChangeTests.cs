using System.Security.Cryptography;
using System.Text.Json;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.FileSystem;
using Xunit;
namespace SpaceInspector.IntegrationTests;

public sealed class CrossVolumeFactAttribute : FactAttribute
{
    public CrossVolumeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SPACE_INSPECTOR_CROSS_VOLUME_TEST_ROOT") is null)
            Skip = "Set SPACE_INSPECTOR_CROSS_VOLUME_TEST_ROOT to an owned test directory on a volume different from TEMP.";
    }
}
public sealed class CrossVolumeFileChangeTests : IDisposable
{
    private readonly string _sourceArea = Path.Combine(Path.GetTempPath(), "SI-cross-" + Guid.NewGuid().ToString("N"));
    private string? _targetArea;
    private readonly WindowsVolumeIdentityService _volumes = new();
    private async Task<(WorkSession Session, IndexedFile File, WorkFileActions Actions, FileChangePlan Plan)> Seed()
    {
        _targetArea = Path.Combine(Environment.GetEnvironmentVariable("SPACE_INSPECTOR_CROSS_VOLUME_TEST_ROOT")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_targetArea);
        var folder = Path.Combine(_sourceArea, "files"); Directory.CreateDirectory(folder);
        Assert.NotEqual(_volumes.GetIdentity(folder), _volumes.GetIdentity(_targetArea));
        var source = Path.Combine(folder, "sample.jpg");
        var data = new byte[3 * 1024 * 1024 + 7]; new Random(42).NextBytes(data); File.WriteAllBytes(source, data);
        var spec = new WorkSpecification { Mode = TargetMode.Folders, Roots = [folder, _targetArea], Extensions = ["jpg"], Recursive = true,
            Volumes = new() { [Path.GetPathRoot(folder)!] = _volumes.GetIdentity(folder), [Path.GetPathRoot(_targetArea)!] = _volumes.GetIdentity(_targetArea) } }.Normalize();
        var session = new WorkSession(Path.Combine(_sourceArea, "jobs", "job"), new() { Specification = spec }, _volumes);
        await session.ExecuteAsync(true, null, default);
        var file = Assert.Single((await session.Result!.SearchAsync(new(), 0, 100)).Files);
        File.WriteAllText(Path.Combine(session.DirectoryPath, "kept-files.json"), JsonSerializer.Serialize(new[] { source }));
        var actions = new WorkFileActions(session, _volumes);
        var plan = await actions.PrepareAsync(file, Path.Combine(_targetArea, "moved.jpg"));
        Assert.True(plan.IsCrossVolume); Assert.True(plan.RemainsInScope);
        return (session, file, actions, plan);
    }
    [CrossVolumeFact]
    public async Task RealCrossVolumeMoveVerifiesBytesAndUpdatesDeviceIdentityAndKeep()
    {
        var (session, file, actions, plan) = await Seed();
        var hash = SHA256.HashData(File.ReadAllBytes(file.FullPath));
        File.SetAttributes(file.FullPath, FileAttributes.Hidden | FileAttributes.Archive);
        await actions.ExecuteAsync(plan);
        Assert.False(File.Exists(file.FullPath)); Assert.False(File.Exists(plan.TemporaryPath));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(plan.TargetPath)));
        Assert.True((File.GetAttributes(plan.TargetPath) & FileAttributes.Hidden) != 0);
        foreach (var db in new[] { session.Index, session.Result! })
        {
            var moved = Assert.Single((await db.SearchAsync(new(), 0, 100)).Files);
            Assert.Equal(plan.TargetPath, moved.FullPath); Assert.Equal(plan.TargetVolume, moved.VolumeIdentifier);
            Assert.NotEqual(file.PhysicalId, moved.PhysicalId); Assert.Equal("Normal", moved.ScanStatus);
            Assert.True(await new WindowsFileFingerprintService(_volumes).IsCurrentAsync(new(moved.FileId, moved.FullPath, moved.FileSize!.Value, moved.ModifiedTimeUtc!, moved.VolumeIdentifier, moved.PhysicalId), default));
        }
        Assert.Equal(new[] { plan.TargetPath }, JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(session.DirectoryPath, "kept-files.json"))));
        Assert.Equal(0, await actions.RecoverAsync());
    }
    [CrossVolumeFact]
    public async Task CancelDuringCopyKeepsSourceAndRemovesOnlyOwnedPartial()
    {
        var (_, file, actions, plan) = await Seed(); using var cts = new CancellationTokenSource();
        var progress = new InlineProgress(p => { if (p.Stage == "복사") cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.ExecuteAsync(plan, cts.Token, progress));
        Assert.True(File.Exists(file.FullPath)); Assert.False(File.Exists(plan.TargetPath)); Assert.False(File.Exists(plan.TemporaryPath));
        Assert.Equal(0, await actions.RecoverAsync());
    }
    [CrossVolumeFact]
    public async Task CrashAfterPublishKeepsBothFilesAndRecoveryNeverDeletesOriginal()
    {
        var (session, file, actions, plan) = await Seed();
        await Assert.ThrowsAsync<IOException>(() => Task.Run(() => WindowsFileChange.CopyAndMove(plan, _volumes, session.DirectoryPath, record => {
            WriteRecord(session, record);
            if (record.State == "Published") throw new IOException("Synthetic crash before original removal");
        }, null, default)));
        Assert.True(File.Exists(file.FullPath)); Assert.True(File.Exists(plan.TargetPath));
        Assert.Equal(0, await actions.RecoverAsync()); Assert.NotEmpty(actions.RecoveryNotes);
        Assert.True(File.Exists(file.FullPath)); Assert.True(File.Exists(plan.TargetPath));
        Assert.Equal(file.FullPath, Assert.Single((await session.Result!.SearchAsync(new(), 0, 100)).Files).FullPath);
    }
    [CrossVolumeFact]
    public async Task RecoveryAfterOriginalRemovalReconcilesBothDatabasesIdempotently()
    {
        var (session, file, actions, plan) = await Seed();
        var completed = await Task.Run(() => WindowsFileChange.CopyAndMove(plan, _volumes, session.DirectoryPath, r => WriteRecord(session, r), null, default));
        Assert.False(File.Exists(file.FullPath));
        await session.Index.ApplyFileChangeAsync(completed);
        Assert.Equal(1, await actions.RecoverAsync()); Assert.Equal(0, await actions.RecoverAsync());
        Assert.Equal(plan.TargetPath, Assert.Single((await session.Result!.SearchAsync(new(), 0, 100)).Files).FullPath);
    }
    [CrossVolumeFact]
    public async Task AlternateDataStreamsAreRejectedInsteadOfSilentlyDiscarded()
    {
        var (_, file, actions, plan) = await Seed();
        File.WriteAllText(file.FullPath + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        await Assert.ThrowsAsync<IOException>(() => actions.ExecuteAsync(plan));
        Assert.True(File.Exists(file.FullPath)); Assert.False(File.Exists(plan.TargetPath));
        Assert.Contains("ZoneId=3", File.ReadAllText(file.FullPath + ":Zone.Identifier"));
    }
    [CrossVolumeFact]
    public async Task LateTargetCollisionNeverOverwritesAndKeepsSource()
    {
        var (session, file, actions, plan) = await Seed();
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => Task.Run(() => WindowsFileChange.CopyAndMove(plan, _volumes, session.DirectoryPath, record => {
            WriteRecord(session, record);
            if (record.State == "Verified") File.WriteAllBytes(plan.TargetPath, [9]);
        }, null, default)));
        Assert.True(File.Exists(file.FullPath)); Assert.Equal(new byte[] {9}, File.ReadAllBytes(plan.TargetPath));
        Assert.False(File.Exists(plan.TemporaryPath)); Assert.Equal(0, await actions.RecoverAsync());
    }
    [CrossVolumeFact]
    public async Task CancellationDuringVerificationDoesNotPublishOrDeleteSource()
    {
        var (_, file, actions, plan) = await Seed(); using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => actions.ExecuteAsync(plan, cts.Token,
            new InlineProgress(p => { if (p.Stage == "복사본 SHA-256 검증") cts.Cancel(); })));
        Assert.True(File.Exists(file.FullPath)); Assert.False(File.Exists(plan.TargetPath)); Assert.False(File.Exists(plan.TemporaryPath));
    }
    [CrossVolumeFact]
    public async Task CrossVolumeUndoCopiesBackWithVerificationAndNewIdentity()
    {
        var (session, file, actions, plan) = await Seed();
        await actions.ExecuteAsync(plan);
        var moved = Assert.Single((await session.Result!.SearchAsync(new(), 0, 100)).Files);
        var undo = await actions.PrepareUndoAsync(moved); Assert.True(undo.IsCrossVolume);
        await actions.ExecuteAsync(undo);
        Assert.True(File.Exists(file.FullPath)); Assert.False(File.Exists(plan.TargetPath));
        var restored = Assert.Single((await session.Result.SearchAsync(new(), 0, 100)).Files);
        Assert.Equal(file.FullPath, restored.FullPath); Assert.Equal(file.VolumeIdentifier, restored.VolumeIdentifier);
        Assert.NotEqual(moved.PhysicalId, restored.PhysicalId);
        Assert.Equal(0, await actions.RecoverAsync());
    }
    [CrossVolumeFact]
    public async Task UndoRejectsChangedContentEvenWithOriginalSizeAndTimestamp()
    {
        var (session, file, actions, plan) = await Seed(); await actions.ExecuteAsync(plan);
        var moved = Assert.Single((await session.Result!.SearchAsync(new(), 0, 100)).Files);
        var modified = File.GetLastWriteTimeUtc(moved.FullPath);
        var bytes = File.ReadAllBytes(moved.FullPath); bytes[0] ^= 1; File.WriteAllBytes(moved.FullPath, bytes);
        File.SetLastWriteTimeUtc(moved.FullPath, modified);
        var undo = await actions.PrepareUndoAsync(moved);
        await Assert.ThrowsAsync<IOException>(() => actions.ExecuteAsync(undo));
        Assert.False(File.Exists(file.FullPath)); Assert.Equal(bytes, File.ReadAllBytes(moved.FullPath));
        Assert.False(File.Exists(undo.TemporaryPath)); Assert.Equal(0, await actions.RecoverAsync());
    }
    [CrossVolumeFact]
    public async Task BatchUndoVerifiesTwoFilesAcrossRealVolumes()
    {
        var (session, file, actions, plan) = await Seed();
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(file.FullPath)!, "second.jpg"), [3, 2, 1]);
        await session.ExecuteAsync(true, null, default);
        var files = (await session.Result!.SearchAsync(new(), 0, 100)).Files;
        var forward = await actions.PrepareBatchAsync(files.Select(f => new FileChangeRequest(f, Path.Combine(Path.GetDirectoryName(plan.TargetPath)!, f.FileName))).ToArray());
        Assert.All(await actions.ExecuteBatchAsync(forward), o => Assert.Equal("Completed", o.State));
        var undo = await actions.PrepareUndoBatchAsync((await session.Result.SearchAsync(new(), 0, 100)).Files);
        Assert.Equal(2, undo.Count); Assert.All(undo, p => Assert.True(p.IsCrossVolume));
        Assert.All(await actions.ExecuteBatchAsync(undo), o => Assert.Equal("Completed", o.State));
        Assert.All(files, f => Assert.True(File.Exists(f.FullPath)));
        Assert.All(forward, p => Assert.False(File.Exists(p.TargetPath)));
    }
    [Fact]
    public void CorruptCopyIsRejectedByFullContentVerification()
    {
        using var source = new MemoryStream(new byte[] {1, 2, 3, 4}); using var target = new CorruptStream();
        Assert.Throws<IOException>(() => WindowsFileChange.CopyAndVerify(source, target, source.Length, null, default));
    }
    private sealed class CorruptStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count)
        { var read = base.Read(buffer, offset, count); if (read > 0) buffer[offset] ^= 1; return read; }
    }
    private sealed class InlineProgress(Action<FileChangeProgress> action) : IProgress<FileChangeProgress>
    { public void Report(FileChangeProgress value) => action(value); }
    private static void WriteRecord(WorkSession session, FileChangeRecord record) => File.WriteAllText(
        Path.Combine(session.DirectoryPath, "file-actions", record.Plan.Id.ToString("N") + ".json"), JsonSerializer.Serialize(record));
    public void Dispose()
    {
        // Only GUID directories created by this fixture, never the configured parent.
        if (_targetArea is not null && Directory.Exists(_targetArea)) Directory.Delete(_targetArea, true);
        if (Directory.Exists(_sourceArea)) Directory.Delete(_sourceArea, true);
    }
}
