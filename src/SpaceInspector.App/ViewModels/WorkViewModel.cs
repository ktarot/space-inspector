using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Data;
using Microsoft.Win32;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.FileSystem;

namespace SpaceInspector.App.ViewModels;

public sealed record SavedWork(string Directory, string Label) { public override string ToString() => Label; }
public sealed record PendingCategory(string Key, string Label, int Count);

public sealed class WorkViewModel : ObservableObject, IDisposable
{
    private readonly string _jobs;
    private readonly WindowsVolumeIdentityService _volumes = new();
    private WorkSession? _session;
    private PermissionWorker? _permissions;
    private CancellationTokenSource? _cancellation;
    private Task? _execution;
    private readonly Stopwatch _watch = new();
    private readonly DispatcherTimer _timer;
    private bool _busy, _folderMode = true, _initializing;
    private string _status = "초기 설정 준비 중", _stage = "", _detail = "";
    private int _filePage, _groupPage, _memberPage;
    private long _fileTotal;
    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<PendingItem> Pending { get; } = [];
    public ObservableCollection<PendingCategory> PendingCategories { get; } = [];
    public ObservableCollection<PendingItem> PendingDetails { get; } = [];
    public ObservableCollection<SavedWork> History { get; } = [];
    public ObservableCollection<IndexedFile> Files { get; } = [];
    public ObservableCollection<DuplicateGroup> Groups { get; } = [];
    public ObservableCollection<DuplicateMember> Members { get; } = [];
    public string? SelectedFolder { get; set; }
    private PendingItem? _selectedPending;
    private PendingCategory? _selectedCategory;
    public PendingItem? SelectedPending { get => _selectedPending; set { _selectedPending = value; Changed(); RefreshCommands(); } }
    public PendingCategory? SelectedCategory { get => _selectedCategory; set { _selectedCategory = value; Changed(); LoadPendingDetails(); } }
    public SavedWork? SelectedHistory { get; set; }
    private DuplicateGroup? _selectedGroup;
    private DuplicateMember? _selectedMember;
    private IndexedFile? _selectedFile;
    public DuplicateGroup? SelectedGroup { get => _selectedGroup; set { _selectedGroup = value; Members.Clear(); SelectedMember = null; _memberPage = 0; Changed(); RefreshCommands(); if (value is not null) _ = LoadMembersAsync(); } }
    public DuplicateMember? SelectedMember { get => _selectedMember; set { _selectedMember = value; if (value is not null) { _selectedFile = null; Changed(nameof(SelectedFile)); } Changed(); } }
    public IndexedFile? SelectedFile { get => _selectedFile; set { _selectedFile = value; if (value is not null) { _selectedMember = null; Changed(nameof(SelectedMember)); } Changed(); } }
    public bool Recursive { get; set; } = true;
    public bool Images { get; set; } = true;
    public bool Videos { get; set; }
    public bool Audio { get; set; }
    public bool Temporary { get; set; }
    public string ExtraExtensions { get; set; } = "";
    public string NameFilter { get; set; } = "";
    public bool FolderMode { get => _folderMode; set { _folderMode = value; Changed(); Changed(nameof(DriveMode)); } }
    public bool DriveMode { get => !_folderMode; set { if (value) FolderMode = false; } }
    public bool Busy { get => _busy; private set { _busy = value; Changed(); Changed(nameof(CanConfigure)); RefreshCommands(); } }
    public bool CanConfigure => !Busy && !_initializing;
    public bool Active => Busy || _initializing;
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string Stage { get => _stage; private set { _stage = value; Changed(); } }
    public string Detail { get => _detail; private set { _detail = value; Changed(); } }
    public string Elapsed => (Busy ? _watch.Elapsed : TimeSpan.FromMilliseconds(_session?.Record.ElapsedMilliseconds ?? 0)).ToString(@"hh\:mm\:ss");
    public string ReadVolume => $"읽기 {(Busy ? _permissions?.BytesRead ?? 0 : _session?.Record.BytesRead ?? 0):N0} 바이트 · 권한 작업은 파일 처리 후 합산";
    public string PendingSummary => $"응답 대기 {Pending.Count(p => p.State == "응답 대기"):N0}건 · 재시도 {Pending.Count(p => p.State is "재시도 예약" or "처리 중"):N0}건 · 제외/건너뜀 {Pending.Count(p => p.State is "보호 규칙으로 제외" or "건너뜀"):N0}건 — ‘응답 대기 · 제외’ 탭에서 처리";
    public bool CanPageMembers => SelectedGroup?.FileCount > 100;
    public bool CanMoveSelectedToRecycle => SelectedGroup?.Status == "ConfirmedDuplicate" && SelectedGroup.DataCount > 1 && SelectedMember is { Status: "ConfirmedDuplicate", PhysicalId: not null };
    public string PageText => $"파일 {_filePage + 1}페이지 / {_fileTotal:N0}개 · 중복 그룹 {_groupPage + 1}페이지 · 그룹 내 파일 {_memberPage + 1}페이지 (100개씩)";
    public string ScopeText => _session is null ? "선택한 대상에 대해서만 탐색하고 비교합니다. 기존 전체 인덱스는 사용하지 않습니다."
        : $"작업 {_session.Record.Started:yyyy-MM-dd HH:mm:ss} · {(_session.Record.Specification.Recursive ? "하위 폴더 포함" : "바로 아래 파일만")} · {string.Join(", ", _session.Record.Specification.Extensions)}\n{string.Join(" / ", _session.Record.Specification.Roots)}";
    public AsyncCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveFolderCommand { get; }
    public AsyncCommand RetryCommand { get; }
    public RelayCommand SkipCommand { get; }
    public AsyncCommand PermissionCommand { get; }
    public AsyncCommand OpenHistoryCommand { get; }
    public AsyncCommand FilterCommand { get; }
    public AsyncCommand PreviousFilesCommand { get; }
    public AsyncCommand NextFilesCommand { get; }
    public AsyncCommand PreviousGroupsCommand { get; }
    public AsyncCommand NextGroupsCommand { get; }
    public AsyncCommand LoadMembersCommand { get; }
    public AsyncCommand PreviousMembersCommand { get; }
    public AsyncCommand NextMembersCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand RevealCommand { get; }
    public AsyncCommand RecycleDuplicateCommand { get; }

    public WorkViewModel(string jobs)
    {
        _jobs = jobs;
        void Error(Exception ex) => Status = "작업 오류 · " + ex.Message;
        StartCommand = new(StartAsync, Error, () => CanConfigure);
        CancelCommand = new(() => { _cancellation?.Cancel(); Status = "중단 요청 · 저장된 결과는 유지합니다."; }, () => Busy && _cancellation is not null);
        AddFolderCommand = new(() =>
        {
            var picker = new OpenFolderDialog { Title = "작업할 폴더 선택", Multiselect = true };
            if (picker.ShowDialog() == true) foreach (var folder in picker.FolderNames)
                if (!Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Folders.Add(folder);
        }, () => CanConfigure && FolderMode);
        RemoveFolderCommand = new(() => { if (SelectedFolder is not null) Folders.Remove(SelectedFolder); }, () => CanConfigure);
        RetryCommand = new(async () =>
        {
            if (_session is null || SelectedPending is null) return;
            _session.Decide(SelectedPending.Path, true); SyncPending();
            if (!Busy) await ExecuteAsync(false);
        }, Error, () => _session is not null && !_initializing);
        SkipCommand = new(() => { if (_session is not null && SelectedPending is not null) { _session.Decide(SelectedPending.Path, false); SyncPending(); if (!Busy) Status = _session.Record.Status; } });
        PermissionCommand = new(async () =>
        {
            var item = SelectedPending; var session = _session; var permissions = _permissions;
            if (item is null || session is null || permissions is null || item.State == "보호 규칙으로 제외") return;
            Status = "선택 항목 권한 요청 · 다른 항목의 작업은 계속됩니다. Windows 승인 창을 확인하세요.";
            await permissions.ApproveAsync(item.Path);
            if (!ReferenceEquals(session, _session)) return;
            session.Decide(item.Path, true); SyncPending();
            if (!Busy && _cancellation?.IsCancellationRequested != true) await ExecuteAsync(false);
        }, Error, () => _session is not null && !_initializing);
        OpenHistoryCommand = new(OpenHistoryAsync, Error, () => CanConfigure);
        FilterCommand = new(async () => { _filePage = 0; await BrowseAsync(); }, Error, () => CanConfigure && _session is not null);
        PreviousFilesCommand = new(async () => { _filePage--; await BrowseAsync(); }, Error, () => CanConfigure && _filePage > 0);
        NextFilesCommand = new(async () => { _filePage++; await BrowseAsync(); }, Error, () => CanConfigure && (_filePage + 1L) * 100 < _fileTotal);
        PreviousGroupsCommand = new(async () => { _groupPage--; await BrowseAsync(); }, Error, () => CanConfigure && _groupPage > 0);
        NextGroupsCommand = new(async () => { _groupPage++; await BrowseAsync(); }, Error, () => CanConfigure && Groups.Count == 100);
        LoadMembersCommand = new(async () => { _memberPage = 0; await LoadMembersAsync(); }, Error, () => CanConfigure);
        PreviousMembersCommand = new(async () => { _memberPage--; await LoadMembersAsync(); }, Error, () => CanConfigure && _memberPage > 0);
        NextMembersCommand = new(async () => { _memberPage++; await LoadMembersAsync(); }, Error, () => CanConfigure && Members.Count == 100);
        CopyCommand = new(() => { var path = SelectedMember?.FullPath ?? SelectedFile?.FullPath; if (path is not null) Clipboard.SetText(path); }, () => CanConfigure);
        RevealCommand = new(() =>
        {
            try
            {
                var path = SelectedMember?.FullPath ?? SelectedFile?.FullPath;
                var volume = SelectedMember?.VolumeIdentifier ?? SelectedFile?.VolumeIdentifier;
                if (path is null) return;
                if (volume is null || _volumes.GetIdentity(path) != volume || !File.Exists(path)) throw new IOException("원래 장치/파일을 확인할 수 없습니다.");
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; start.ArgumentList.Add("/select," + path); Process.Start(start);
            }
            catch (Exception ex) { Error(ex); }
        }, () => CanConfigure);
        RecycleDuplicateCommand = new(RecycleSelectedDuplicateAsync, Error, () => CanConfigure && CanMoveSelectedToRecycle);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        { Changed(nameof(Elapsed)); Changed(nameof(ReadVolume)); SyncPending(); RefreshCommands(); }, Dispatcher.CurrentDispatcher);
    }

    public async Task InitializeAsync()
    {
        _initializing = true; Changed(nameof(CanConfigure)); Changed(nameof(Active));
        try
        {
            Stage = "초기 설정 · 드라이브 확인";
            foreach (var drive in await new LocalDriveService().GetLocalDrivesAsync()) Drives.Add(new(drive));
            Stage = "저장된 작업 목록 확인";
            await LoadHistoryAsync(); Status = "준비 완료 · 대상과 파일 종류를 정한 후 탐색·비교 시작을 누르세요.";
        }
        catch (Exception ex) { Status = "초기화 오류 · " + ex.Message; }
        finally { _initializing = false; Stage = "대기"; Changed(nameof(CanConfigure)); Changed(nameof(Active)); RefreshCommands(); }
    }

    private WorkSpecification Capture()
    {
        var extensions = new List<string>();
        if (Images) extensions.AddRange("jpg jpeg gif png bmp webp tif tiff heic avif".Split(' '));
        if (Videos) extensions.AddRange("avi mp4 mpg mpeg wmv fla flv mov mkv webm m4v".Split(' '));
        if (Audio) extensions.AddRange("wav m4a mp3 wma aac flac ogg opus".Split(' '));
        if (Temporary) extensions.AddRange("tmp temp cache bak".Split(' '));
        extensions.AddRange(ExtraExtensions.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        var roots = FolderMode ? Folders.ToArray() : Drives.Where(d => d.IsSelected).Select(d => d.Snapshot.RootPath).ToArray();
        var spec = new WorkSpecification { Mode = FolderMode ? TargetMode.Folders : TargetMode.Drives, Roots = roots, Recursive = Recursive, Extensions = extensions.ToArray() }.Normalize();
        foreach (var root in spec.Roots) spec.Volumes[Path.GetPathRoot(root)!] = _volumes.GetIdentity(root);
        return spec;
    }

    private async Task StartAsync()
    {
        var spec = Capture();
        var directory = Path.Combine(_jobs, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        SetSession(directory, new() { Specification = spec });
        Files.Clear(); Groups.Clear(); Members.Clear(); _filePage = _groupPage = _memberPage = 0;
        await ExecuteAsync(true);
    }
    private void SetSession(string directory, WorkRecord record)
    {
        _permissions?.Dispose(); _permissions = new(directory, record.Specification, _volumes);
        _session = new(directory, record, _volumes, _permissions, _permissions.Source);
        Changed(nameof(ScopeText)); SyncPending();
    }

    private async Task ExecuteAsync(bool initial)
    {
        if (_session is null || Busy) return;
        _cancellation?.Dispose(); _cancellation = new(); Busy = true; Changed(nameof(Active)); _watch.Restart();
        var session = _session;
        long discovered = session.Record.Discovered;
        var progress = new Progress<WorkUpdate>(p =>
        {
            if (!Busy || !ReferenceEquals(_session, session)) return;
            discovered = Math.Max(session.Record.Discovered, Math.Max(discovered, p.Files));
            Stage = p.Stage; Detail = $"발견 {discovered:N0}개 · 분석 처리 {p.Processed:N0}회 · 미검증 {p.Unverified:N0}회\n{p.Path}";
        });
        try
        {
            Status = "선택한 범위 탐색·비교 중 · 응답 대기 항목이 있어도 계속합니다.";
            _execution = session.ExecuteAsync(initial, progress, _cancellation.Token); await _execution;
            Status = session.Record.Status;
        }
        catch (Exception ex) { Status = session.Record.Status + " · " + ex.Message; }
        finally
        {
            _execution = null; _watch.Stop(); Stage = "저장된 결과 불러오기";
            try { await LoadResultsAsync(); await LoadHistoryAsync(); }
            catch (Exception ex) { Status += " · 결과 조회 오류: " + ex.Message; }
            Busy = false; Changed(nameof(Active)); Stage = "작업 종료 · 결과는 검사 시점 기준"; SyncPending();
        }
        if (session.HasRetries && !_cancellation.IsCancellationRequested) await ExecuteAsync(false);
    }

    private void SyncPending()
    {
        if (_session is null) return;
        if (!Busy) Detail = $"발견 {_session.Record.Discovered:N0}개 · 분석 처리 {_session.Record.Processed:N0}회 · 미검증 {_session.Record.Unverified:N0}회";
        var items = _session.Pending;
        if (Pending.SequenceEqual(items)) return;
        var selected = SelectedPending?.Path;
        Pending.Clear(); foreach (var item in items) Pending.Add(item);
        var categories = items.GroupBy(CategoryKey).Select(g => new PendingCategory(g.Key, CategoryLabel(g.Key), g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Label, StringComparer.CurrentCulture).ToArray();
        PendingCategories.Clear(); foreach (var category in categories) PendingCategories.Add(category);
        SelectedCategory = PendingCategories.FirstOrDefault(c => c.Key == SelectedCategory?.Key) ?? PendingCategories.FirstOrDefault();
        Changed(nameof(PendingSummary));
        SelectedPending = Pending.FirstOrDefault(p => p.Path == selected); Changed(nameof(SelectedPending));
    }
    private async Task LoadResultsAsync()
    {
        if (_session is null) return;
        var page = await _session.Index.SearchAsync(new FileSearch { Name = NameFilter }, _filePage, 100);
        _fileTotal = page.TotalCount; Files.Clear(); foreach (var file in page.Files) Files.Add(file);
        Groups.Clear(); Members.Clear(); SelectedGroup = null; SelectedMember = null; SelectedFile = null;
        Changed(nameof(SelectedGroup)); Changed(nameof(SelectedMember)); Changed(nameof(SelectedFile));
        if (_session.Result is { } result) foreach (var group in await result.GetDuplicateGroupsAsync(_groupPage, 100)) Groups.Add(group);
        Changed(nameof(PageText)); RefreshCommands();
    }
    private async Task BrowseAsync()
    {
        Busy = true; try { await LoadResultsAsync(); } finally { Busy = false; }
    }
    private async Task LoadMembersAsync()
    {
        if (_session?.Result is not { } result || SelectedGroup is not { } group) return;
        Busy = true;
        try
        {
            var members = await result.GetDuplicateMembersAsync(group.GroupId, _memberPage, 100);
            Members.Clear(); foreach (var member in members) Members.Add(member);
            Changed(nameof(PageText)); Changed(nameof(CanPageMembers));
        }
        finally { Busy = false; }
    }
    private async Task LoadHistoryAsync()
    {
        var entries = await Task.Run(() =>
        {
            Directory.CreateDirectory(_jobs); var list = new List<SavedWork>();
            foreach (var file in Directory.EnumerateFiles(_jobs, "job.json", SearchOption.AllDirectories).OrderDescending().Take(100))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<WorkRecord>(File.ReadAllText(file))!;
                    list.Add(new(Path.GetDirectoryName(file)!, $"{record.Started:yyyy-MM-dd HH:mm:ss} · {record.Status}"));
                }
                catch (Exception ex) when (ex is IOException or JsonException) { list.Add(new(Path.GetDirectoryName(file)!, "읽기 실패 · " + ex.Message)); }
            }
            return list;
        });
        History.Clear(); foreach (var entry in entries) History.Add(entry);
    }

    private static string CategoryKey(PendingItem item)
    {
        var path = item.Path;
        var folder = Path.GetDirectoryName(path) ?? path;
        var message = item.Reason;
        var kind = message.Contains("Access to the path", StringComparison.OrdinalIgnoreCase) || message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            ? "접근 거부" : message.Contains("장치", StringComparison.OrdinalIgnoreCase) || message.Contains("volume", StringComparison.OrdinalIgnoreCase)
                ? "장치 변경/연결" : message.Contains("reparse", StringComparison.OrdinalIgnoreCase) || message.Contains("링크", StringComparison.OrdinalIgnoreCase)
                    ? "링크·클라우드 보호" : message;
        return kind + "\n" + folder;
    }
    private static string CategoryLabel(string key)
    {
        var parts = key.Split('\n', 2);
        return parts.Length == 2 ? $"{parts[0]} · {parts[1]}" : key;
    }
    private void LoadPendingDetails()
    {
        PendingDetails.Clear();
        if (SelectedCategory is null) return;
        foreach (var item in Pending.Where(p => CategoryKey(p) == SelectedCategory.Key)) PendingDetails.Add(item);
        if (SelectedPending is null || !Pending.Contains(SelectedPending)) SelectedPending = PendingDetails.FirstOrDefault();
    }

    private async Task RecycleSelectedDuplicateAsync()
    {
        var member = SelectedMember; var group = SelectedGroup; var result = _session?.Result;
        if (member is null || group is null || result is null || !CanMoveSelectedToRecycle) return;
        var answer = MessageBox.Show(
            $"이 파일을 Windows 휴지통으로 보냅니까?\n\n{member.FullPath}\n\n그룹의 다른 확인된 복사본은 유지됩니다. 휴지통에서 복원할 수 있습니다.",
            "중복 파일을 휴지통으로 이동", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            if (member.VolumeIdentifier is null || _volumes.GetIdentity(member.FullPath) != member.VolumeIdentifier || !File.Exists(member.FullPath))
                throw new IOException("원래 장치나 파일이 달라졌습니다. 파일을 이동하지 않았습니다.");
            var candidate = new SpaceInspector.Core.Models.DuplicateCandidate(member.FileId, member.FullPath, member.FileSize,
                File.GetLastWriteTimeUtc(member.FullPath).ToString("O"), member.VolumeIdentifier, member.PhysicalId);
            if (_permissions is null || !await _permissions.IsCurrentAsync(candidate, default))
                throw new IOException("중복 확인 후 파일이 바뀌었습니다. 파일을 이동하지 않았습니다.");
            var live = new List<DuplicateMember>();
            for (var page = 0; live.Count < group.FileCount; page++)
            {
                var members = await result.GetDuplicateMembersAsync(group.GroupId, page, 100);
                if (members.Count == 0) break;
                live.AddRange(members);
                if (members.Count < 100) break;
            }
            var remainingData = live.Where(f => f.FullPath != member.FullPath && f.PhysicalId is not null
                    && File.Exists(f.FullPath) && f.VolumeIdentifier is not null && _volumes.GetIdentity(f.FullPath) == f.VolumeIdentifier)
                .Select(f => f.PhysicalId).Distinct(StringComparer.Ordinal).Count();
            if (remainingData < 1) throw new IOException("보존할 복사본을 확인할 수 없습니다. 파일을 이동하지 않았습니다.");
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(member.FullPath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            await result.MarkRecycledAsync(member.FileId, member.FullPath);
            if (_session?.Result is { } resultIndex) await resultIndex.MarkRecycledAsync(member.FileId, member.FullPath);
            Status = "선택한 파일을 휴지통으로 옮겼습니다. 같은 그룹의 다른 파일은 유지했습니다.";
            SelectedMember = null;
            await LoadResultsAsync();
        }
        catch (Exception ex) { Status = "휴지통 이동 실패 · " + ex.Message; }
    }
    private async Task OpenHistoryAsync()
    {
        if (SelectedHistory is null) return;
        Busy = true;
        try
        {
            var record = JsonSerializer.Deserialize<WorkRecord>(await File.ReadAllTextAsync(Path.Combine(SelectedHistory.Directory, "job.json")))!;
            if (record.Status == "실행 중") record = record with { Status = "앱 종료로 중단 · 일부 결과" };
            SetSession(SelectedHistory.Directory, record);
            FolderMode = record.Specification.Mode == TargetMode.Folders;
            Recursive = record.Specification.Recursive; Images = Videos = Audio = Temporary = false;
            ExtraExtensions = string.Join(", ", record.Specification.Extensions);
            Folders.Clear(); if (FolderMode) foreach (var root in record.Specification.Roots) Folders.Add(root);
            foreach (var drive in Drives) drive.IsSelected = !FolderMode && record.Specification.Roots.Contains(drive.Snapshot.RootPath, StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { nameof(Recursive), nameof(Images), nameof(Videos), nameof(Audio), nameof(Temporary), nameof(ExtraExtensions) }) Changed(name);
            _filePage = _groupPage = _memberPage = 0;
            await _session!.Index.InitializeAsync();
            if (_session.Result is { } result) await result.RecoverWorkResultAsync();
            Status = record.Status; await LoadResultsAsync();
        }
        finally { Busy = false; }
    }
    private void RefreshCommands()
    {
        StartCommand.Refresh(); CancelCommand.Refresh(); AddFolderCommand.Refresh(); RemoveFolderCommand.Refresh(); RetryCommand.Refresh(); PermissionCommand.Refresh();
        OpenHistoryCommand.Refresh(); FilterCommand.Refresh(); PreviousFilesCommand.Refresh(); NextFilesCommand.Refresh();
        PreviousGroupsCommand.Refresh(); NextGroupsCommand.Refresh(); LoadMembersCommand.Refresh(); PreviousMembersCommand.Refresh(); NextMembersCommand.Refresh(); CopyCommand.Refresh(); RevealCommand.Refresh();
        RecycleDuplicateCommand?.Refresh();
    }
    public async Task StopAsync() { _cancellation?.Cancel(); while (Active) await Task.Delay(50); }
    public void Dispose() { _timer.Stop(); _permissions?.Dispose(); _cancellation?.Dispose(); }
}
