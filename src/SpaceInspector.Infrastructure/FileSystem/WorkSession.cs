using System.Collections.Concurrent;
using System.Text.Json;
using SpaceInspector.Core.DuplicateDetection;
using SpaceInspector.Core.Indexing;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.Database;

namespace SpaceInspector.Infrastructure.FileSystem;

public sealed record PendingItem(string Path, string Reason, string State = "응답 대기");
public sealed record WorkUpdate(string Stage, long Files = 0, long Processed = 0, long Unverified = 0, string? Path = null);
public sealed record WorkRecord
{
    public WorkSpecification Specification { get; init; } = new();
    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;
    public string Status { get; init; } = "준비 중";
    public string? ResultFile { get; init; }
    public PendingItem[] Pending { get; init; } = [];
    public long Discovered { get; init; }
    public long Processed { get; init; }
    public long Unverified { get; init; }
    public long ElapsedMilliseconds { get; init; }
    public long BytesRead { get; init; }
}

// Each job owns an index. Analysis attempts use independent snapshots, preserving prior results.
public sealed class WorkSession
{
    private readonly IVolumeIdentityService _volumes;
    private readonly IFileFingerprintService _fingerprints;
    private readonly Func<WorkSpecification, IFileMetadataSource> _source;
    private readonly ConcurrentDictionary<string, PendingItem> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _retry = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _saveLock = new();
    public string DirectoryPath { get; }
    public WorkRecord Record { get; private set; }
    public PendingItem[] Pending => _pending.Values.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    public bool HasRetries => !_retry.IsEmpty;
    public SqliteIndexDatabase Index => new(Path.Combine(DirectoryPath, "index.db"), _volumes, Record.Specification.Recursive);
    public SqliteIndexDatabase? Result => Record.ResultFile is { } name ? new(Path.Combine(DirectoryPath, name), _volumes) : null;

    public WorkSession(string directory, WorkRecord record, IVolumeIdentityService volumes,
        IFileFingerprintService? fingerprints = null, Func<WorkSpecification, IFileMetadataSource>? source = null)
    {
        DirectoryPath = Path.GetFullPath(directory); Record = record with { Specification = record.Specification.Normalize() };
        _volumes = volumes; _fingerprints = fingerprints ?? new WindowsFileFingerprintService(volumes);
        _source = source ?? (s => new MetadataOnlyFileSource([Path.GetDirectoryName(DirectoryPath)!, AppContext.BaseDirectory], volumes, s));
        foreach (var item in record.Pending) _pending[item.Path] = item with { State = item.State is "재시도 예약" or "처리 중" ? "응답 대기" : item.State };
    }

    public void Decide(string path, bool retry)
    {
        if (!_pending.TryGetValue(path, out var item) || item.State is "재시도 예약" or "처리 중" or "보호 규칙으로 제외") return;
        _pending[path] = item with { State = retry ? "재시도 예약" : "건너뜀" };
        if (retry) _retry.Enqueue(path);
        if (Record.Status.StartsWith("처리 가능한 항목 완료", StringComparison.Ordinal)
            && !_pending.Values.Any(p => p.State == "응답 대기") && !HasRetries)
            Record = Record with { Status = "완료 (제외·건너뜀 확인)" };
        Save();
    }

    public void Save()
    {
        lock (_saveLock)
        {
            Directory.CreateDirectory(DirectoryPath);
            Record = Record with { Pending = Pending };
            var path = Path.Combine(DirectoryPath, "job.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Record, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
    }

    public async Task ExecuteAsync(bool initial, IProgress<WorkUpdate>? progress, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("이미 실행 중인 작업입니다.");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bytesBefore = (_fingerprints as PermissionWorker)?.BytesRead ?? 0;
        try
        {
            Record = Record with { Status = "실행 중" }; Save();
            progress?.Report(new("작업 인덱스 준비 중"));
            await Index.InitializeAsync(token);
            if (initial) await Scan(Record.Specification.Roots, progress, token);
            do
            {
                while (_retry.TryDequeue(out var path))
                {
                    token.ThrowIfCancellationRequested();
                    _pending[path] = _pending[path] with { State = "처리 중" };
                    var root = File.Exists(path) ? Path.GetDirectoryName(path)! : path;
                    await Scan([root], progress, token);
                    if (_pending[path].State == "처리 중") _pending[path] = _pending[path] with { State = "재시도 완료" };
                    Save();
                }
                token.ThrowIfCancellationRequested();
                var name = $"result-{Guid.NewGuid():N}.db";
                await Index.BackupAsync(Path.Combine(DirectoryPath, name), token);
                Record = Record with { ResultFile = name }; Save();
                var analysis = new DuplicateAnalysisService(Result!, _fingerprints);
                var result = await analysis.AnalyzeAsync(new(IncludeProtected: true),
                    new InlineProgress<DuplicateProgress>(p => progress?.Report(new(p.Stage, Processed: p.Processed, Unverified: p.Unverified))), token);
                Record = Record with { Processed = result.Processed, Unverified = result.Unverified };
                for (var page = 0; ; page++)
                {
                    var members = await Result!.GetDuplicateMembersAsync("unverified", page, 100);
                    foreach (var member in members) _pending[member.FullPath] = new(member.FullPath, member.Error ?? "파일을 검증할 수 없습니다.");
                    if (members.Count < 100) break;
                }
                if (result.Status == "Cancelled") throw new OperationCanceledException(token);
            } while (HasRetries);
            Record = Record with { Status = Pending.Any(p => p.State == "응답 대기") ? "처리 가능한 항목 완료 · 응답 대기 있음" : "완료 (제외·건너뜀 확인)" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Record = Record with { Status = "중단 · 일부 결과 (미탐색 영역의 파일 수는 알 수 없음)" };
            while (_retry.TryDequeue(out var path)) _pending[path] = _pending[path] with { State = "응답 대기" };
            foreach (var pair in _pending.Where(p => p.Value.State == "처리 중")) _pending[pair.Key] = pair.Value with { State = "응답 대기" };
        }
        catch
        {
            Record = Record with { Status = "오류로 중단 · 저장된 결과 유지" }; throw;
        }
        finally
        {
            Record = Record with { ElapsedMilliseconds = Record.ElapsedMilliseconds + watch.ElapsedMilliseconds,
                BytesRead = Record.BytesRead + (((_fingerprints as PermissionWorker)?.BytesRead ?? 0) - bytesBefore) };
            try { Save(); } finally { _gate.Release(); }
        }
    }

    private async Task Scan(string[] roots, IProgress<WorkUpdate>? progress, CancellationToken token)
    {
        var notification = System.Diagnostics.Stopwatch.StartNew();
        var firstIssue = true;
        var source = new ObservedSource(_source(Record.Specification), issue =>
        {
            if (issue.Status is "AccessDenied" or "Unavailable") _pending[issue.Path] = new(issue.Path, issue.Message);
            else _pending[issue.Path] = new(issue.Path, issue.Message, "보호 규칙으로 제외");
            if (firstIssue || notification.ElapsedMilliseconds >= 150)
            {
                progress?.Report(new("탐색 중 · 접근 가능한 항목 계속 처리", Path: issue.Path));
                firstIssue = false; notification.Restart();
            }
        });
        var result = await new FileIndexingService(source, Index, Record.Specification.Recursive).ScanAsync(roots,
            new InlineProgress<ScanProgress>(p => progress?.Report(new("탐색", p.Files, Path: p.CurrentPath))), token);
        Record = Record with { Discovered = (await Index.GetSummaryAsync()).FileCount };
        Save();
        if (result.Status == "Cancelled") throw new OperationCanceledException(token);
        if (result.Status == "Failed") throw new IOException("탐색에 실패했습니다. 저장된 결과와 대기 목록을 확인하세요.");
    }

    private sealed class ObservedSource(IFileMetadataSource inner, Action<ScanIssue> issue) : IFileMetadataSource
    {
        public IEnumerable<ScanObservation> Enumerate(IReadOnlyList<string> roots, CancellationToken cancellationToken)
        {
            foreach (var item in inner.Enumerate(roots, cancellationToken))
            {
                if (item.Issue is { } problem) issue(problem);
                yield return item;
            }
        }
    }
}

public sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}
