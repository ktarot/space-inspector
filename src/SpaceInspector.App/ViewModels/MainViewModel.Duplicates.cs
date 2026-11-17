using System.Collections.ObjectModel;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.App.ViewModels;

public sealed partial class MainViewModel
{
    private IDuplicateAnalysisService? _duplicates;
    private IDuplicateRepository? _duplicateRepository;
    private CancellationTokenSource? _duplicateCancellation;
    private Task<DuplicateResult>? _duplicateTask;
    private Task? _duplicateBrowseTask;
    private bool _analyzing;
    private int _groupPage, _memberPage;
    private DuplicateGroup? _selectedGroup;
    private DuplicateMember? _selectedMember;
    public bool IsAnalyzing { get => _analyzing; private set { _analyzing = value; Changed(); Changed(nameof(IsWorking)); CancelCommand.Refresh(); } }
    public int DuplicateLevelIndex { get; set; } = 2;
    public bool IncludeProtectedDuplicates { get; set; }
    public string[] DuplicateLevels { get; } = ["크기 후보만", "부분 해시까지", "전체 SHA-256 검증"];
    public ObservableCollection<DuplicateGroup> DuplicateGroups { get; } = [];
    public ObservableCollection<DuplicateMember> DuplicateMembers { get; } = [];
    public string DuplicatePageText => $"그룹 {_groupPage + 1} 페이지 · 파일 {_memberPage + 1} 페이지 (각 100개)";
    public DuplicateGroup? SelectedDuplicateGroup
    {
        get => _selectedGroup;
        set { _selectedGroup = value; Changed(); DuplicateMembers.Clear(); _memberPage = 0; SelectedDuplicateMember = null; RefreshDuplicateCommands(); }
    }
    public DuplicateMember? SelectedDuplicateMember
    {
        get => _selectedMember;
        set
        {
            _selectedMember = value; Changed();
            if (value is not null) SelectedFile = new(value.FileId, System.IO.Path.GetFileName(value.FullPath), value.FullPath, value.FileSize, null, "Normal", null, value.VolumeIdentifier);
            else SelectedFile = null;
        }
    }
    public AsyncCommand AnalyzeDuplicatesCommand { get; private set; } = null!;
    public AsyncCommand LoadDuplicateMembersCommand { get; private set; } = null!;
    public AsyncCommand PreviousGroupsCommand { get; private set; } = null!;
    public AsyncCommand NextGroupsCommand { get; private set; } = null!;
    public AsyncCommand PreviousMembersCommand { get; private set; } = null!;
    public AsyncCommand NextMembersCommand { get; private set; } = null!;

    private void ConfigureDuplicates(IDuplicateAnalysisService? duplicates, IDuplicateRepository? repository)
    {
        _duplicates = duplicates; _duplicateRepository = repository;
        void Error(Exception ex) => Status = "중복 분석 오류: " + ex.Message;
        AnalyzeDuplicatesCommand = new(AnalyzeDuplicatesAsync, Error, () => !IsBusy && _ready && _duplicates is not null);
        LoadDuplicateMembersCommand = new(() => BrowseDuplicatesAsync(false, 0), Error, () => !IsBusy && SelectedDuplicateGroup is not null);
        PreviousGroupsCommand = new(() => BrowseDuplicatesAsync(true, -1), Error, () => !IsBusy && _groupPage > 0);
        NextGroupsCommand = new(() => BrowseDuplicatesAsync(true, 1), Error, () => !IsBusy && DuplicateGroups.Count == 100);
        PreviousMembersCommand = new(() => BrowseDuplicatesAsync(false, -1), Error, () => !IsBusy && _memberPage > 0);
        NextMembersCommand = new(() => BrowseDuplicatesAsync(false, 1), Error, () => !IsBusy && SelectedDuplicateGroup is not null && DuplicateMembers.Count == 100);
    }
    private void RefreshDuplicateCommands()
    {
        AnalyzeDuplicatesCommand?.Refresh(); LoadDuplicateMembersCommand?.Refresh(); PreviousGroupsCommand?.Refresh(); NextGroupsCommand?.Refresh(); PreviousMembersCommand?.Refresh(); NextMembersCommand?.Refresh();
    }
    private async Task LoadDuplicateGroupsAsync(CancellationToken cancellationToken = default)
    {
        if (_duplicateRepository is null) return;
        var groups = await _duplicateRepository.GetDuplicateGroupsAsync(_groupPage, 100, cancellationToken);
        SelectedDuplicateGroup = null;
        DuplicateGroups.Clear(); foreach (var group in groups) DuplicateGroups.Add(group);
        Changed(nameof(DuplicatePageText));
    }
    private async Task BrowseDuplicatesAsync(bool groups, int direction)
    {
        if (IsBusy || _duplicateRepository is null) return;
        IsBusy = true; _duplicateCancellation = new(); IsAnalyzing = true;
        var cancellation = _duplicateCancellation;
        var oldGroupPage = _groupPage; var oldMemberPage = _memberPage;
        try
        {
            if (groups) { _groupPage = Math.Max(0, _groupPage + direction); _duplicateBrowseTask = LoadDuplicateGroupsAsync(cancellation.Token); await _duplicateBrowseTask; }
            else if (SelectedDuplicateGroup is { } selected)
            {
                _memberPage = Math.Max(0, _memberPage + direction);
                var query = _duplicateRepository.GetDuplicateMembersAsync(selected.GroupId, _memberPage, 100, cancellation.Token);
                _duplicateBrowseTask = query;
                var members = await query;
                DuplicateMembers.Clear(); foreach (var member in members) DuplicateMembers.Add(member);
            }
            Changed(nameof(DuplicatePageText));
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            _groupPage = oldGroupPage; _memberPage = oldMemberPage;
            Status = "중복 결과 조회를 취소했습니다."; Changed(nameof(DuplicatePageText));
        }
        finally { IsAnalyzing = false; _duplicateBrowseTask = null; _duplicateCancellation.Dispose(); _duplicateCancellation = null; IsBusy = false; }
    }
    public async Task AnalyzeDuplicatesAsync()
    {
        if (IsBusy || !_ready || _duplicates is null) return;
        IsBusy = true; _duplicateCancellation = new(); IsAnalyzing = true;
        var cancellation = _duplicateCancellation;
        Status = "저장 인덱스 전체에서 중복 분석 중 · 원본 변경 없음 · 취소 가능";
        ProgressText = "크기 후보를 준비하고 있습니다. 파일 크기에 따라 오래 걸릴 수 있습니다.";
        var progress = new Progress<DuplicateProgress>(p =>
        {
            if (IsAnalyzing && ReferenceEquals(cancellation, _duplicateCancellation)) ProgressText = $"{p.Stage} · 처리 {p.Processed:N0}회 · 미검증 {p.Unverified:N0}회";
        });
        try
        {
            _duplicateTask = _duplicates.AnalyzeAsync(new((DuplicateLevel)DuplicateLevelIndex, IncludeProtectedDuplicates), progress, cancellation.Token);
            var result = await _duplicateTask;
            ProgressText = $"중복 분석 #{result.RunId} · 처리 {result.Processed:N0}회 · 미검증 {result.Unverified:N0}회";
            Status = result.Status == "Completed" ? "중복 분석 완료 · 같은 실제 파일의 하드링크는 논리적 중복량에서 제외합니다. 실제 확보량은 미확정입니다."
                : "중복 분석 취소 · 이번 분석 결과를 확정하지 않았습니다.";
            _groupPage = 0; await LoadDuplicateGroupsAsync(cancellation.Token); SelectedTab = 3;
        }
        catch (OperationCanceledException) { Status = "중복 분석을 취소했습니다."; }
        catch (Exception) when (cancellation.IsCancellationRequested) { Status = "중복 결과 조회를 취소했습니다."; }
        catch (Exception ex) { Status = "중복 분석 오류: " + ex.Message; }
        finally { IsAnalyzing = false; _duplicateCancellation.Dispose(); _duplicateCancellation = null; _duplicateTask = null; IsBusy = false; }
    }
}
