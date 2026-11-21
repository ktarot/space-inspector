using System.Text.Json;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
namespace SpaceInspector.Infrastructure.FileSystem;

public sealed class WorkFileActions(WorkSession session, IVolumeIdentityService volumes)
{
    private string ActionsDirectory => Path.Combine(session.DirectoryPath, "file-actions");
    public List<string> RecoveryNotes { get; } = [];
    private string KeepFile => Path.Combine(session.DirectoryPath, "kept-files.json");
    private FileStream Lock()
    {
        Directory.CreateDirectory(ActionsDirectory);
        return new FileStream(Path.Combine(ActionsDirectory, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public async Task<FileChangePlan> PrepareAsync(IndexedFile file, string target)
    {
        using var gate = Lock(); await RecoverCoreAsync();
        return await PrepareCoreAsync(file, target);
    }
    private async Task<FileChangePlan> PrepareCoreAsync(IndexedFile file, string target)
    {
        if (file.ScanStatus != "Normal" || !session.Record.Specification.AllowsFile(file.FullPath)
            || file.FileSize is null || file.ModifiedTimeUtc is null || file.VolumeIdentifier is null)
            throw new IOException("현재 작업 범위의 정상 파일만 변경할 수 있습니다. 필요한 범위를 다시 탐색하세요.");
        target = WindowsFileChange.NormalizeTarget(target);
        var candidate = new DuplicateCandidate(file.FileId, file.FullPath, file.FileSize.Value, file.ModifiedTimeUtc, file.VolumeIdentifier, file.PhysicalId);
        var targetVolume = volumes.GetIdentity(target) ?? throw new IOException("대상 장치를 확인할 수 없습니다.");
        var physical = await Task.Run(() => WindowsFileChange.Inspect(candidate, target, volumes, session.DirectoryPath, targetVolume));
        var plan = new FileChangePlan(Guid.NewGuid(), candidate with { PhysicalId = physical }, target,
            RemainsInScope(target, targetVolume), session.Record.ResultFile, LoadKept().Contains(file.FullPath)) { TargetVolumeIdentifier = targetVolume };
        await CheckDatabasesAsync(plan); return plan;
    }
    public async Task<FileChangePlan> PrepareUndoAsync(IndexedFile file)
    {
        using var gate = Lock(); await RecoverCoreAsync();
        return await PrepareUndoCoreAsync(file);
    }
    private async Task<FileChangePlan> PrepareUndoCoreAsync(IndexedFile file)
    {
        var records = ReadRecords();
        var undone = records.Where(r => r.State == "Completed" && r.Plan.UndoOf is not null).Select(r => r.Plan.UndoOf!.Value).ToHashSet();
        var original = records.Where(r => r.State == "Completed" && r.Plan.UndoOf is null && !undone.Contains(r.Plan.Id)
            && r.Plan.Source.FileId == file.FileId && r.Plan.ResultFile == session.Record.ResultFile
            && string.Equals(r.Plan.TargetPath, file.FullPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.UpdatedUtc).FirstOrDefault()?.Plan
            ?? throw new IOException("이 파일에서 되돌릴 수 있는 완료 기록이 없습니다. 현재 작업의 이동·이름 변경만 지원합니다.");
        var plan = new FileChangePlan(Guid.NewGuid(), original.Destination, original.Source.FullPath,
            RemainsInScope(original.Source.FullPath, original.Source.VolumeIdentifier!), session.Record.ResultFile, LoadKept().Contains(file.FullPath))
            { UndoOf = original.Id, TargetVolumeIdentifier = original.Source.VolumeIdentifier, ExpectedSourceHash = original.ContentHash };
        ValidatePlan(plan); await CheckDatabasesAsync(plan);
        await Task.Run(() => WindowsFileChange.Inspect(plan.Source, plan.TargetPath, volumes, session.DirectoryPath, plan.TargetVolume));
        return plan;
    }
    public async Task<IReadOnlyList<FileChangePlan>> PrepareUndoBatchAsync(IReadOnlyList<IndexedFile> files, CancellationToken token = default)
    {
        using var gate = Lock(); await RecoverCoreAsync();
        if (files.Count is < 1 or > 100 || files.Select(f => f.FileId).Distinct().Count() != files.Count)
            throw new IOException("현재 페이지에서 서로 다른 파일을 1~100개 선택하세요.");
        var plans = new List<FileChangePlan>();
        foreach (var file in files.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try { plans.Add(await PrepareUndoCoreAsync(file)); }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            { throw new IOException("되돌리기 사전 검사 실패 · " + file.FullPath + " · " + ex.Message + " · 아무 파일도 변경하지 않았습니다.", ex); }
        }
        CheckBatchPaths(plans.Select(p => (p.Source.FullPath, p.TargetPath)).ToArray());
        return plans;
    }
    private FileChangeRecord[] ReadRecords() => Directory.EnumerateFiles(ActionsDirectory, "*.json")
        .Select(path => JsonSerializer.Deserialize<FileChangeRecord>(File.ReadAllText(path)) ?? throw new IOException("변경 기록을 읽을 수 없습니다.")).ToArray();
    private void ValidateUndo(FileChangePlan plan)
    {
        var records = ReadRecords();
        var original = records.SingleOrDefault(r => r.Plan.Id == plan.UndoOf && r.State == "Completed")?.Plan;
        if (original is null || original.UndoOf is not null || original.ResultFile != session.Record.ResultFile
            || plan.Source != original.Destination || plan.TargetPath != original.Source.FullPath
            || plan.TargetVolume != original.Source.VolumeIdentifier || plan.ExpectedSourceHash != original.ContentHash
            || records.Any(r => r.State == "Completed" && r.Plan.UndoOf == original.Id))
            throw new IOException("되돌리기 계획과 완료 기록이 다릅니다. 파일을 다시 선택하세요.");
    }
    public async Task ExecuteAsync(FileChangePlan plan, CancellationToken token = default, IProgress<FileChangeProgress>? progress = null)
    {
        using var gate = Lock(); await RecoverCoreAsync();
        await ExecuteCoreAsync(plan, token, progress);
    }
    private async Task ExecuteCoreAsync(FileChangePlan plan, CancellationToken token, IProgress<FileChangeProgress>? progress)
    {
        token.ThrowIfCancellationRequested(); ValidatePlan(plan); await CheckDatabasesAsync(plan);
        plan = plan with { WasKept = LoadKept().Contains(plan.Source.FullPath) };
        if (plan.IsCrossVolume)
            plan = await Task.Run(() => WindowsFileChange.CopyAndMove(plan, volumes, session.DirectoryPath, Save, progress, token));
        else
            await Task.Run(() => WindowsFileChange.Move(plan.Source, plan.TargetPath, volumes, session.DirectoryPath,
                () => { token.ThrowIfCancellationRequested(); Save(new(plan, "Pending", DateTimeOffset.UtcNow)); }));
        await ReconcileAsync(plan);
    }
    public async Task<IReadOnlyList<FileChangePlan>> PrepareBatchAsync(IReadOnlyList<FileChangeRequest> requests, CancellationToken token = default)
    {
        using var gate = Lock(); await RecoverCoreAsync();
        CheckBatchPaths(requests.Select(r => (r.File.FullPath, r.TargetPath)).ToArray());
        var plans = new List<FileChangePlan>();
        foreach (var request in requests) { token.ThrowIfCancellationRequested(); plans.Add(await PrepareCoreAsync(request.File, request.TargetPath)); }
        return plans;
    }
    public async Task<IReadOnlyList<FileChangeOutcome>> ExecuteBatchAsync(IReadOnlyList<FileChangePlan> plans,
        CancellationToken token = default, IProgress<FileChangeProgress>? progress = null)
    {
        using var gate = Lock(); await RecoverCoreAsync();
        CheckBatchPaths(plans.Select(p => (p.Source.FullPath, p.TargetPath)).ToArray());
        // Preflight every item again before the first mutation. Races still fail without overwriting.
        foreach (var plan in plans)
        {
            token.ThrowIfCancellationRequested(); ValidatePlan(plan); await CheckDatabasesAsync(plan);
            await Task.Run(() => WindowsFileChange.Inspect(plan.Source, plan.TargetPath, volumes, session.DirectoryPath, plan.TargetVolume));
        }
        var outcomes = new List<FileChangeOutcome>(); bool stopped = false;
        foreach (var plan in plans)
        {
            if (stopped) { outcomes.Add(new(plan, "NotProcessed")); continue; }
            try
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(plan.Source.FullPath, $"{outcomes.Count + 1}/{plans.Count} 파일 변경", 0, plan.Source.Size));
                await ExecuteCoreAsync(plan, token, progress); outcomes.Add(new(plan, "Completed"));
            }
            catch (OperationCanceledException) { outcomes.Add(new(plan, "Cancelled", "중단 요청 · 원본 유지")); stopped = true; }
            catch (Exception ex) { outcomes.Add(new(plan, "Failed", ex.Message + " · 파일 변경 기록 확인 필요")); stopped = true; }
        }
        var reports = Path.Combine(ActionsDirectory, "reports"); Directory.CreateDirectory(reports);
        WriteDurable(Path.Combine(reports, Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(outcomes));
        return outcomes;
    }
    private static void CheckBatchPaths((string Source, string Target)[] paths)
    {
        if (paths.Length is < 1 or > 100) throw new IOException("현재 페이지에서 1~100개 파일을 선택하세요.");
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, target) in paths)
            if (!sources.Add(source) || !targets.Add(WindowsFileChange.NormalizeTarget(target))) throw new IOException("원본 또는 변경 경로가 중복됩니다. 아무 파일도 변경하지 않았습니다.");
        if (targets.Overlaps(sources)) throw new IOException("변경 경로가 선택한 원본과 겹칩니다. 이름 교환이나 동일 경로 변경은 지원하지 않습니다.");
    }
    public async Task<int> RecoverAsync()
    {
        using var gate = Lock(); return await RecoverCoreAsync();
    }
    private async Task<int> RecoverCoreAsync()
    {
        var count = 0; RecoveryNotes.Clear();
        foreach (var path in Directory.EnumerateFiles(ActionsDirectory, "*.json").Order())
        {
            var record = JsonSerializer.Deserialize<FileChangeRecord>(await File.ReadAllTextAsync(path))
                ?? throw new IOException("파일 변경 기록을 읽을 수 없습니다.");
            if (record.Plan.IsCrossVolume)
            {
                if (record.State == "Completed") continue;
                if (record.State is "NotMoved" or "CopyRetained")
                {
                    AddRetainedNotes(record); continue;
                }
                count += await RecoverCopyAsync(record); continue;
            }
            if (record.State != "Pending") continue;
            var plan = record.Plan; ValidatePlan(plan);
            var fingerprints = new WindowsFileFingerprintService(volumes);
            var targetCurrent = await fingerprints.IsCurrentAsync(plan.Source with { FullPath = plan.TargetPath }, default);
            var sourceCurrent = await fingerprints.IsCurrentAsync(plan.Source, default);
            if (!File.Exists(plan.Source.FullPath) && targetCurrent)
            {
                await ReconcileAsync(plan); count++;
            }
            else if (sourceCurrent && !targetCurrent)
                Save(record with { State = "NotMoved", UpdatedUtc = DateTimeOffset.UtcNow });
            else throw new IOException("이전 파일 변경을 자동 확인할 수 없습니다. 원본/변경 경로와 장치를 확인하세요: " + plan.Source.FullPath + " → " + plan.TargetPath);
        }
        return count;
    }
    private bool RemainsInScope(string target, string volume) => session.Record.Specification.AllowsFile(target)
        && session.Record.Specification.Volumes.TryGetValue(Path.GetPathRoot(target)!, out var expected) && expected == volume;
    private void AddRetainedNotes(FileChangeRecord record)
    {
        if (record.State == "CopyRetained") RecoveryNotes.Add("원본과 복사본을 함께 보존했습니다. 확인 후 정리하세요: " + record.Plan.TargetPath);
        if (File.Exists(record.Plan.TemporaryPath)) RecoveryNotes.Add("중단된 임시 복사본을 보존했습니다. 확인 후 정리하세요: " + record.Plan.TemporaryPath);
    }
    private async Task<int> RecoverCopyAsync(FileChangeRecord record)
    {
        var plan = record.Plan; ValidatePlan(plan);
        if (volumes.GetIdentity(plan.Source.FullPath) != plan.Source.VolumeIdentifier || volumes.GetIdentity(plan.TargetPath) != plan.TargetVolume)
            throw new IOException("이전 이동의 원본/대상 장치를 연결하고 파일 변경 기록을 다시 확인하세요.");
        var fingerprints = new WindowsFileFingerprintService(volumes);
        var sourceCurrent = await fingerprints.IsCurrentAsync(plan.Source, default);
        bool sourceAbsent;
        try { File.GetAttributes(plan.Source.FullPath); sourceAbsent = false; }
        catch (FileNotFoundException) { sourceAbsent = true; }
        var targetValid = false;
        if (plan.TargetPhysicalId is not null && plan.ContentHash is not null)
        {
            var result = await fingerprints.ComputeAsync(plan.Destination, DuplicateLevel.FullHash, true, default);
            targetValid = result.Hash == plan.ContentHash && result.PhysicalId == plan.TargetPhysicalId;
        }
        if (sourceAbsent && targetValid) { await ReconcileAsync(plan); return 1; }
        if (sourceCurrent)
        {
            record = record with { State = targetValid || (plan.ContentHash is not null && File.Exists(plan.TargetPath)) ? "CopyRetained" : "NotMoved", UpdatedUtc = DateTimeOffset.UtcNow };
            Save(record); AddRetainedNotes(record); return 0;
        }
        throw new IOException("이전 이동의 파일 상태가 달라 자동 복구하지 않았습니다: " + plan.Source.FullPath + " → " + plan.TargetPath);
    }
    private void ValidatePlan(FileChangePlan plan)
    {
        if (plan.Id == Guid.Empty || plan.Source.PhysicalId is null || plan.Source.VolumeIdentifier is null
            || plan.ResultFile != session.Record.ResultFile || (plan.UndoOf is null && !session.Record.Specification.AllowsFile(plan.Source.FullPath))
            || plan.RemainsInScope != RemainsInScope(plan.TargetPath, plan.TargetVolume))
            throw new IOException("작업 설정과 파일 변경 계획이 다릅니다. 변경을 다시 준비하세요.");
        if (plan.UndoOf is not null) ValidateUndo(plan);
        WindowsFileChange.NormalizeTarget(plan.TargetPath);
    }
    private async Task CheckDatabasesAsync(FileChangePlan plan)
    {
        await session.Index.CheckFileChangeAsync(plan);
        if (session.Result is { } result) await result.CheckFileChangeAsync(plan);
    }
    private async Task ReconcileAsync(FileChangePlan plan)
    {
        await session.Index.ApplyFileChangeAsync(plan);
        if (session.Result is { } result) await result.ApplyFileChangeAsync(plan);
        var kept = LoadKept();
        if (kept.Remove(plan.Source.FullPath) || plan.WasKept) kept.Add(plan.TargetPath);
        WriteDurable(KeepFile, JsonSerializer.Serialize(kept));
        Save(new(plan, "Completed", DateTimeOffset.UtcNow));
    }
    private HashSet<string> LoadKept() => File.Exists(KeepFile)
        ? new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(KeepFile)) ?? [], StringComparer.OrdinalIgnoreCase)
        : new(StringComparer.OrdinalIgnoreCase);
    private void Save(FileChangeRecord record) => WriteDurable(Path.Combine(ActionsDirectory, record.Plan.Id.ToString("N") + ".json"), JsonSerializer.Serialize(record));
    private static void WriteDurable(string path, string text)
    {
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text); stream.Write(bytes); stream.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
    }
}
