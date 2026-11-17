using System.Collections.ObjectModel;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IDriveService _drives;
    private readonly IIndexDatabase _database;
    private readonly IScanRepository? _repository;
    private readonly IFileIndexingService? _indexing;
    private CancellationTokenSource? _cancellation;
    private Task<ScanResult>? _scanTask;
    private string _status = "초기화 준비 중", _indexStatus = "인덱스 확인 중", _progressText = "검사 대기 중", _pageText = "";
    private bool _busy, _scanning, _ready;
    private int _page;
    private long _total;
    private string? _selectedFolder;
    public const int PageSize = 200;
    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public ObservableCollection<string> Folders { get; } = [];
    public ObservableCollection<IndexedFile> Files { get; } = [];
    public ObservableCollection<ScanIssue> Issues { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand PreviousPageCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveFolderCommand { get; }
    public RelayCommand SelectAllDrivesCommand { get; }
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string IndexStatus { get => _indexStatus; private set { _indexStatus = value; Changed(); } }
    public string ProgressText { get => _progressText; private set { _progressText = value; Changed(); } }
    public string PageText { get => _pageText; private set { _pageText = value; Changed(); } }
    public bool IsBusy { get => _busy; private set { _busy = value; Changed(); Changed(nameof(CanEditScope)); RefreshCommands(); } }
    public bool IsScanning { get => _scanning; private set { _scanning = value; Changed(); CancelCommand.Refresh(); } }
    public bool CanEditScope => !IsBusy;
    public string? SelectedFolder { get => _selectedFolder; set { _selectedFolder = value; Changed(); RemoveFolderCommand.Refresh(); } }

    public MainViewModel(IDriveService drives, IIndexDatabase database, IFileIndexingService? indexing = null,
        IScanRepository? repository = null, Func<IReadOnlyList<string>>? pickFolders = null)
    {
        _drives = drives;
        _database = database;
        _repository = repository ?? database as IScanRepository;
        _indexing = indexing;
        void Error(Exception ex) { Status = "작업 오류: " + ex.Message; }
        RefreshCommand = new(InitializeAsync, Error, () => !IsBusy);
        ScanCommand = new(RunScanAsync, Error, () => !IsBusy && _ready && _indexing is not null && (Drives.Any(d => d.IsSelected) || Folders.Count > 0));
        PreviousPageCommand = new(() => ChangePageAsync(-1), Error, () => !IsBusy && _page > 0);
        NextPageCommand = new(() => ChangePageAsync(1), Error, () => !IsBusy && ((long)_page + 1) * PageSize < _total);
        CancelCommand = new(CancelScan, () => IsScanning && _cancellation?.IsCancellationRequested == false);
        AddFolderCommand = new(() =>
        {
            try
            {
                foreach (var folder in pickFolders?.Invoke() ?? [])
                    if (!Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Folders.Add(folder);
            }
            catch (Exception ex) { Error(ex); }
        }, () => !IsBusy && pickFolders is not null);
        RemoveFolderCommand = new(() => { if (SelectedFolder is { } folder) Folders.Remove(folder); }, () => !IsBusy && SelectedFolder is not null);
        SelectAllDrivesCommand = new(() => { foreach (var drive in Drives) drive.IsSelected = drive.Snapshot.IsReady; }, () => !IsBusy);
        Folders.CollectionChanged += (_, _) => ScanCommand.Refresh();
    }

    private void RefreshCommands()
    {
        RefreshCommand.Refresh(); ScanCommand.Refresh(); PreviousPageCommand.Refresh(); NextPageCommand.Refresh();
        AddFolderCommand.Refresh(); RemoveFolderCommand.Refresh(); SelectAllDrivesCommand.Refresh(); CancelCommand.Refresh();
    }

    public async Task InitializeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _ready = false;
        Status = "로컬 드라이브와 저장 인덱스를 확인하고 있습니다.";
        try
        {
            await _database.InitializeAsync();
            await LoadPageAsync();
            var snapshots = await _drives.GetLocalDrivesAsync();
            var selected = Drives.Where(d => d.IsSelected).Select(d => d.Snapshot.RootPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Drives.Clear();
            foreach (var snapshot in snapshots)
            {
                var drive = new DriveViewModel(snapshot) { IsSelected = snapshot.IsReady && selected.Contains(snapshot.RootPath) };
                drive.PropertyChanged += (_, _) => ScanCommand.Refresh();
                Drives.Add(drive);
            }
            _ready = true;
            Status = "준비 완료 · 검사 범위를 선택하거나 저장된 인덱스를 조회하세요.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            IndexStatus = "정보 확인 실패";
            Status = "초기화 오류: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    public async Task RunScanAsync()
    {
        if (IsBusy || !_ready || _indexing is null) return;
        var roots = Drives.Where(d => d.IsSelected).Select(d => d.Snapshot.RootPath).Concat(Folders).ToArray();
        if (roots.Length == 0) { Status = "검사할 드라이브 또는 폴더를 선택하세요."; return; }
        IsBusy = true;
        _cancellation = new();
        var scanCancellation = _cancellation;
        IsScanning = true;
        Issues.Clear();
        Status = "메타데이터 검사 중 · 전체 파일 수는 탐색이 끝나면 확정됩니다.";
        ProgressText = "검사 준비 중";
        var progress = new Progress<ScanProgress>(p =>
        {
            if (IsScanning && ReferenceEquals(_cancellation, scanCancellation)) ProgressText = $"파일 {p.Files:N0}개 · 오류 {p.Errors:N0}개 · 건너뜀 {p.Skipped:N0}개\n{p.CurrentPath}";
        });
        try
        {
            _scanTask = _indexing.ScanAsync(roots, progress, _cancellation.Token);
            var result = await _scanTask;
            IsScanning = false;
            ProgressText = $"파일 {result.Files:N0}개 · 오류 {result.Errors:N0}개 · 건너뜀 {result.Skipped:N0}개";
            Status = result.Status switch
            {
                "Cancelled" => "검사를 취소했습니다. 저장된 결과는 유지하며, 미검사 파일을 없음으로 처리하지 않습니다.",
                "Completed" => "검사가 완료되었습니다. 같은 범위로 다시 검사하면 인덱스를 갱신합니다.",
                "CompletedWithIssues" => "검사 완료 · 오류/건너뜀 경로를 확인하세요. 해당 영역의 기존 인덱스는 보존합니다.",
                _ => "검사 실패 · 저장된 결과는 보존했습니다. 오류 기록을 확인하세요."
            };
            _page = 0;
            await LoadPageAsync();
            if (_repository is not null)
                foreach (var issue in await _repository.GetIssuesAsync(result.ScanId)) Issues.Add(issue);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Status = "검사 오류: " + ex.Message; }
        finally
        {
            IsScanning = false;
            _scanTask = null;
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }

    public void CancelScan()
    {
        _cancellation?.Cancel();
        CancelCommand.Refresh();
        Status = "취소 요청 · 현재 메타데이터 작업과 저장을 마치는 중입니다.";
    }

    public async Task CancelAndWaitAsync()
    {
        CancelScan();
        if (_scanTask is not null) { try { await _scanTask; } catch (Exception) { /* RunScanAsync displays the failure. */ } }
    }

    private async Task ChangePageAsync(int direction)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { _page = Math.Max(0, _page + direction); await LoadPageAsync(); }
        finally { IsBusy = false; }
    }

    private async Task LoadPageAsync()
    {
        var summary = await _database.GetSummaryAsync();
        IndexStatus = summary.FileCount == 0 ? "저장된 인덱스 없음 · 파일 0개" : $"인덱스 파일 {summary.FileCount:N0}개 (없음·접근 제한 상태 포함)";
        if (_repository is null) return;
        var page = await _repository.GetPageAsync(_page, PageSize);
        _total = page.TotalCount;
        Files.Clear();
        foreach (var file in page.Files) Files.Add(file);
        PageText = _total == 0 ? "저장된 파일이 없습니다." : $"{_page + 1:N0} / {(_total + PageSize - 1) / PageSize:N0} 페이지 · 전체 {_total:N0}개";
    }
}
