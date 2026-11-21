using System.IO;
using System.Text.Json;
using System.Windows;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.FileSystem;
namespace SpaceInspector.App.ViewModels;

public sealed partial class WorkViewModel
{
    private bool _fileChanging;
    private CancellationTokenSource? _fileChangeCancellation;
    private IReadOnlyList<IndexedFile> _chosenFiles = [];
    public IReadOnlyList<IndexedFile> ChosenFiles { get => _chosenFiles; set { _chosenFiles = value; Changed(); RefreshCommands(); } }
    public Func<IReadOnlyList<IndexedFile>, bool, IReadOnlyList<FileChangeRequest>?>? ChooseBatchTargets { get; set; }
    public Func<IReadOnlyList<FileChangePlan>, bool>? ConfirmBatchChanges { get; set; }
    public Action<IReadOnlyList<FileChangeOutcome>>? ShowBatchResults { get; set; }
    public Func<IndexedFile, bool, string?>? ChooseFileTarget { get; set; }
    public Func<FileChangePlan, bool> ConfirmFileChange { get; set; } = plan => MessageBox.Show(
        (plan.UndoOf is null ? "" : "완료된 이동·이름 변경을 되돌립니다. 중복 판정은 다시 검사해야 합니다.\n\n")
        + $"현재 경로:\n{plan.Source.FullPath}\n\n변경 경로:\n{plan.TargetPath}\n\n"
        + (plan.RemainsInScope ? "현재 작업 범위 안에서 변경합니다." : "작업 범위 밖으로 이동합니다. 이 파일은 현재 중복 비교 대상에서 제외됩니다.")
        + (plan.IsCrossVolume ? "\n다른 볼륨: 복사본 SHA-256 검증 후 원본을 제거합니다. 대상 폴더의 권한을 상속합니다." : "")
        + "\n보관 지정은 새 경로에 유지됩니다. 변경하시겠습니까?", "파일 변경 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public event Action? BeforeFileChange;
    public event Action? AfterFileChange;
    public AsyncCommand RenameFileCommand { get; private set; } = null!;
    public AsyncCommand MoveFileCommand { get; private set; } = null!;
    public AsyncCommand UndoFileChangeCommand { get; private set; } = null!;
    public AsyncCommand RecoverFileChangesCommand { get; private set; } = null!;
    private void InitializeFileActionCommands()
    {
        bool CanChange() => CanConfigure && _session is not null && SelectedFile is { ScanStatus: "Normal" } && ChooseFileTarget is not null;
        void Error(Exception ex) => Status = "파일 변경 오류 · " + ex.Message;
        RenameFileCommand = new(() => ChangeFileAsync(true), Error, CanChange);
        MoveFileCommand = new(() => ChangeFileAsync(false), Error, CanChange);
        UndoFileChangeCommand = new(UndoFileChangeAsync, Error, () => CanConfigure && _session is not null
            && SelectedFile is not null);
        RecoverFileChangesCommand = new(async () => {
            if (_session is null) return;
            _fileChanging = true; Busy = true; BeforeFileChange?.Invoke();
            try {
                var actions = new WorkFileActions(_session, _volumes);
                var count = await actions.RecoverAsync();
                ReloadKeptPaths(); await LoadResultsAsync(); Status = $"파일 변경 기록 확인 완료 · 복구 {count}건" + (actions.RecoveryNotes.Count == 0 ? "" : " · " + string.Join(" · ", actions.RecoveryNotes));
            } finally { _fileChanging = false; Busy = false; AfterFileChange?.Invoke(); }
        }, Error, () => CanConfigure && _session is not null);
    }
    private async Task ChangeFileAsync(bool renameOnly)
    {
        if (_session is not { } session || SelectedFile is not { } file || ChooseFileTarget is null) return;
        var chosen = ChosenFiles.Where(f => Files.Contains(f)).ToArray();
        _fileChangeCancellation = new();
        _fileChanging = true; Busy = true; BeforeFileChange?.Invoke();
        try
        {
            if (chosen.Length > 1) { await ChangeBatchAsync(session, chosen, renameOnly); return; }
            var target = ChooseFileTarget(file, renameOnly); if (target is null) return;
            Stage = "파일 변경 준비";
            var actions = new WorkFileActions(session, _volumes);
            var plan = await actions.PrepareAsync(file, target);
            _fileChangeCancellation.Token.ThrowIfCancellationRequested();
            if (!ConfirmFileChange(plan)) { Status = "파일 변경을 취소했습니다."; return; }
            Stage = "파일 변경 및 기록 갱신";
            await actions.ExecuteAsync(plan, _fileChangeCancellation.Token, ActionProgress());
            ReloadKeptPaths(); await LoadResultsAsync();
            SelectedFile = Files.FirstOrDefault(f => string.Equals(f.FullPath, target, StringComparison.OrdinalIgnoreCase));
            Status = "파일 변경 완료 · " + target + (plan.RemainsInScope ? "" : " · 작업 범위 밖으로 이동");
        }
        catch (OperationCanceledException) { Status = "파일 변경 중단 · 원본을 보존했습니다."; }
        catch (Exception ex) { Status = "파일 변경 미완료 · " + ex.Message + " · 필요하면 ‘파일 변경 기록 확인’을 사용하세요."; }
        finally { _fileChanging = false; _fileChangeCancellation?.Dispose(); _fileChangeCancellation = null; Busy = false; Stage = "대기"; Detail = ""; AfterFileChange?.Invoke(); }
    }
    private async Task UndoFileChangeAsync()
    {
        if (_session is not { } session || SelectedFile is not { } file) return;
        _fileChangeCancellation = new(); _fileChanging = true; Busy = true; BeforeFileChange?.Invoke();
        try
        {
            Stage = "되돌리기 사전 검사";
            var actions = new WorkFileActions(session, _volumes);
            var chosen = ChosenFiles.Where(f => Files.Contains(f)).ToArray();
            if (chosen.Length > 1)
            {
                var plans = await actions.PrepareUndoBatchAsync(chosen, _fileChangeCancellation.Token);
                await ConfirmAndExecuteBatchAsync(actions, plans, "일괄 되돌리기"); return;
            }
            var plan = await actions.PrepareUndoAsync(file);
            _fileChangeCancellation.Token.ThrowIfCancellationRequested();
            if (!ConfirmFileChange(plan)) { Status = "되돌리기를 취소했습니다."; return; }
            await actions.ExecuteAsync(plan, _fileChangeCancellation.Token, ActionProgress());
            ReloadKeptPaths(); await LoadResultsAsync();
            SelectedFile = Files.FirstOrDefault(f => string.Equals(f.FullPath, plan.TargetPath, StringComparison.OrdinalIgnoreCase));
            Status = "되돌리기 완료 · " + plan.TargetPath + " · 중복 판정은 다시 탐색·비교하세요.";
        }
        catch (OperationCanceledException) { Status = "되돌리기 중단 · 현재 파일을 보존했습니다."; }
        catch (Exception ex) { Status = "되돌리기 미완료 · " + ex.Message + " · 필요하면 ‘파일 변경 기록 확인’을 사용하세요."; }
        finally { _fileChanging = false; _fileChangeCancellation?.Dispose(); _fileChangeCancellation = null; Busy = false; Stage = "대기"; Detail = ""; AfterFileChange?.Invoke(); }
    }
    private IProgress<FileChangeProgress> ActionProgress() => new Progress<FileChangeProgress>(p => {
        if (!_fileChanging) return;
        Stage = p.Stage; Detail = $"{p.Path} · {p.Bytes:N0} / {p.TotalBytes:N0} 바이트";
    });
    private async Task ChangeBatchAsync(WorkSession session, IReadOnlyList<IndexedFile> files, bool renameOnly)
    {
        var requests = ChooseBatchTargets?.Invoke(files, renameOnly); if (requests is null) return;
        var actions = new WorkFileActions(session, _volumes); Stage = "일괄 변경 사전 검사";
        var plans = await actions.PrepareBatchAsync(requests, _fileChangeCancellation!.Token);
        await ConfirmAndExecuteBatchAsync(actions, plans, "일괄 변경");
    }
    private async Task ConfirmAndExecuteBatchAsync(WorkFileActions actions, IReadOnlyList<FileChangePlan> plans, string operation)
    {
        _fileChangeCancellation!.Token.ThrowIfCancellationRequested();
        if (ConfirmBatchChanges?.Invoke(plans) != true) { Status = operation + "을 취소했습니다."; return; }
        var outcomes = await actions.ExecuteBatchAsync(plans, _fileChangeCancellation.Token, ActionProgress());
        ReloadKeptPaths(); await LoadResultsAsync();
        Status = $"{operation} · 완료 {outcomes.Count(o => o.State == "Completed")} · 실패 {outcomes.Count(o => o.State == "Failed")} · 중단 {outcomes.Count(o => o.State == "Cancelled")} · 미처리 {outcomes.Count(o => o.State == "NotProcessed")}";
        if (plans.Any(p => p.UndoOf is not null)) Status += " · 복원한 파일의 중복 판정은 다시 탐색·비교하세요.";
        ShowBatchResults?.Invoke(outcomes);
    }
    private void ReloadKeptPaths()
    {
        _keptPaths = _keepFile is not null && File.Exists(_keepFile)
            ? new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(_keepFile)) ?? [], StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }
}
