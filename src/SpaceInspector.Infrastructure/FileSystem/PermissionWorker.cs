using System.Diagnostics;
using System.Text.Json;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Infrastructure.FileSystem;

public sealed record PermissionRequest(string Kind, string[] Roots, DuplicateCandidate? File = null, DuplicateLevel Level = DuplicateLevel.FullHash);
public sealed record PermissionReply(FileFingerprint? Fingerprint = null, bool Current = false, string? Error = null, long BytesRead = 0);

// A user-approved helper performs read-only operations restricted to a frozen job specification.
// One request at a time; the normal worker keeps processing other paths while UAC is pending.
public sealed class PermissionWorker(string jobDirectory, WorkSpecification specification, IVolumeIdentityService volumes) : IFileFingerprintService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<string> _approved = [];
    private long _bytesRead;
    public long BytesRead => Interlocked.Read(ref _bytesRead);
    private Process? _process;
    private string? _channel;
    public bool Approved(string path) { lock (_approved) return _approved.Any(p => Core.Indexing.ScanScope.Contains(p, path)); }

    public async Task ApproveAsync(string path)
    {
        if (!specification.AllowsDirectory(path) && !specification.AllowsFile(path)) throw new ArgumentException("작업 범위 밖입니다.");
        if (_process is null || _process.HasExited)
        {
            _channel = Path.Combine(jobDirectory, "permission-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_channel);
            File.WriteAllText(Path.Combine(_channel, "scope.json"), JsonSerializer.Serialize(specification));
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("--permission-worker"); start.ArgumentList.Add(_channel); start.ArgumentList.Add(Environment.ProcessId.ToString());
            _process = await Task.Run(() => Process.Start(start)) ?? throw new IOException("권한 작업을 시작하지 못했습니다.");
        }
        lock (_approved) _approved.Add(path);
    }

    private async Task<(PermissionReply Reply, string Output)> Request(PermissionRequest request, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_channel is null || _process is null || _process.HasExited) throw new IOException("권한 작업 연결이 종료되었습니다. 다시 요청하세요.");
            var id = Guid.NewGuid().ToString("N");
            var prefix = Path.Combine(_channel, id);
            File.WriteAllText(prefix + ".tmp", JsonSerializer.Serialize(request));
            File.Move(prefix + ".tmp", prefix + ".request");
            try
            {
                while (!File.Exists(prefix + ".reply"))
                {
                    token.ThrowIfCancellationRequested();
                    if (_process.HasExited) throw new IOException("권한 작업이 종료되었습니다.");
                    await Task.Delay(100, token);
                }
                var reply = JsonSerializer.Deserialize<PermissionReply>(await File.ReadAllTextAsync(prefix + ".reply", token))!;
                File.Delete(prefix + ".reply");
                Interlocked.Add(ref _bytesRead, reply.BytesRead);
                if (reply.Error is not null) throw new IOException(reply.Error);
                return (reply, prefix + ".jsonl");
            }
            catch (OperationCanceledException) { File.WriteAllText(prefix + ".cancel", ""); throw; }
        }
        finally { _gate.Release(); }
    }

    public IFileMetadataSource Source(WorkSpecification scope) => new ElevatedSource(this,
        new MetadataOnlyFileSource([Path.GetDirectoryName(jobDirectory)!, AppContext.BaseDirectory], volumes, scope));

    public async Task<FileFingerprint> ComputeAsync(DuplicateCandidate file, DuplicateLevel level, bool includeProtected, CancellationToken cancellationToken)
    {
        var result = await new WindowsFileFingerprintService(volumes, n => Interlocked.Add(ref _bytesRead, n)).ComputeAsync(file, level, includeProtected, cancellationToken);
        if (result.Status != "Unverified" || !Approved(file.FullPath)) return result;
        try { return (await Request(new("hash", [], file, level), cancellationToken)).Reply.Fingerprint!; }
        catch (IOException ex) { return new("Unverified", Error: ex.Message); }
    }
    public async Task<bool> IsCurrentAsync(DuplicateCandidate file, CancellationToken cancellationToken)
    {
        if (await new WindowsFileFingerprintService(volumes).IsCurrentAsync(file, cancellationToken)) return true;
        if (!Approved(file.FullPath)) return false;
        try { return (await Request(new("current", [], file), cancellationToken)).Reply.Current; }
        catch (IOException) { return false; }
    }

    public void Dispose()
    {
        if (_channel is not null) File.WriteAllText(Path.Combine(_channel, "stop"), "");
        _process?.Dispose();
    }

    private sealed class ElevatedSource(PermissionWorker owner, IFileMetadataSource normal) : IFileMetadataSource
    {
        public IEnumerable<ScanObservation> Enumerate(IReadOnlyList<string> roots, CancellationToken cancellationToken)
        {
            foreach (var root in roots)
            {
                if (!owner.Approved(root))
                {
                    foreach (var item in normal.Enumerate([root], cancellationToken)) yield return item;
                    continue;
                }
                string? output = null; ScanIssue? issue = null;
                try { output = owner.Request(new("scan", [root]), cancellationToken).GetAwaiter().GetResult().Output; }
                catch (IOException ex) { issue = new(root, "AccessDenied", ex.Message); }
                if (issue is not null) { yield return new(null, issue); continue; }
                try
                {
                    foreach (var line in File.ReadLines(output!))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return JsonSerializer.Deserialize<ScanObservation>(line)!;
                    }
                }
                finally { File.Delete(output!); }
            }
        }
    }

    public static async Task RunAsync(string channel, int parentId)
    {
        var scope = JsonSerializer.Deserialize<WorkSpecification>(await File.ReadAllTextAsync(Path.Combine(channel, "scope.json")))!.Normalize();
        using var parent = Process.GetProcessById(parentId);
        var volumes = new WindowsVolumeIdentityService();
        while (!parent.HasExited && !File.Exists(Path.Combine(channel, "stop")))
        {
            foreach (var path in Directory.EnumerateFiles(channel, "*.request"))
            {
                var prefix = Path.Combine(channel, Path.GetFileNameWithoutExtension(path));
                if (File.Exists(prefix + ".reply")) continue;
                using var cancellation = new CancellationTokenSource();
                using var monitor = new Timer(_ =>
                {
                    if (parent.HasExited || File.Exists(prefix + ".cancel") || File.Exists(Path.Combine(channel, "stop"))) cancellation.Cancel();
                }, null, 0, 100);
                PermissionReply reply;
                long bytesRead = 0;
                var fingerprints = new WindowsFileFingerprintService(volumes, n => bytesRead += n);
                try
                {
                    var request = JsonSerializer.Deserialize<PermissionRequest>(await File.ReadAllTextAsync(path))!;
                    if (request.Kind == "scan")
                    {
                        if (request.Roots.Any(r => !scope.AllowsDirectory(r))) throw new IOException("작업 범위 밖입니다.");
                        using var writer = new StreamWriter(prefix + ".jsonl");
                        var source = new MetadataOnlyFileSource([Path.GetDirectoryName(Path.GetDirectoryName(channel)!)!, AppContext.BaseDirectory], volumes, scope);
                        foreach (var item in source.Enumerate(request.Roots, cancellation.Token)) await writer.WriteLineAsync(JsonSerializer.Serialize(item));
                        reply = new();
                    }
                    else
                    {
                        var file = request.File ?? throw new IOException("파일 정보가 없습니다.");
                        if (!scope.AllowsFile(file.FullPath)) throw new IOException("작업 범위 밖입니다.");
                        reply = request.Kind == "hash" ? new(await fingerprints.ComputeAsync(file, request.Level, true, cancellation.Token))
                            : request.Kind == "current" ? new(Current: await fingerprints.IsCurrentAsync(file, cancellation.Token))
                            : throw new IOException("지원하지 않는 작업입니다.");
                    }
                }
                catch (Exception ex) { reply = new(Error: ex.Message); }
                await File.WriteAllTextAsync(prefix + ".reply.tmp", JsonSerializer.Serialize(reply with { BytesRead = bytesRead }));
                File.Delete(path);
                File.Move(prefix + ".reply.tmp", prefix + ".reply");
            }
            await Task.Delay(100);
        }
    }
}
