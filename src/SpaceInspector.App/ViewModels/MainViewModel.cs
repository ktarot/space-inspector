using System.Collections.ObjectModel;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
using System.Globalization;
using System.IO;

namespace SpaceInspector.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDriveService _drives;
    private readonly IIndexDatabase _database;
    private readonly IScanRepository? _repository;
    private readonly IFileIndexingService? _indexing;
    private CancellationTokenSource? _cancellation;
    private Task<ScanResult>? _scanTask;
    private CancellationTokenSource? _queryCancellation;
    private Task<IndexPage>? _queryTask;
    private bool _searching;
    public bool IsSearching { get => _searching; private set { _searching = value; Changed(); Changed(nameof(IsWorking)); CancelCommand.Refresh(); } }
    public bool IsWorking => IsScanning || IsSearching || IsAnalyzing;
    private string _status = "초기화 준비 중", _indexStatus = "인덱스 확인 중", _progressText = "검사 대기 중", _pageText = "";
    private bool _busy, _scanning, _ready;
    private int _page;
    private long _total;
    private string? _selectedFolder;
    private FileSearch _search = new();
    private IndexedFile? _selectedFile;
    private int _selectedTab = 1;
    public int SelectedTab { get => _selectedTab; set { _selectedTab = value; Changed(); } }
    private readonly IFileSearchRepository? _searchRepository;
    public string NameFilter { get; set; } = "";
    public string ExtensionsFilter { get; set; } = "";
    public string MinimumSize { get; set; } = "";
    public string MaximumSize { get; set; } = "";
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }
    public DateTime? ModifiedFrom { get; set; }
    public DateTime? ModifiedTo { get; set; }
    public bool SearchSelectedScope { get; set; }
    public int SortIndex { get; set; }
    public bool SortDescending { get; set; }
    public string[] SortOptions { get; } = ["등록 순서", "파일명", "크기", "수정일", "생성일", "경로"];
    public AsyncCommand SearchCommand { get; }
    public AsyncCommand ResetSearchCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand RevealFileCommand { get; }
    public RelayCommand ReferenceFileCommand { get; }
    public IndexedFile? SelectedFile
    {
        get => _selectedFile;
        set { _selectedFile = value; Changed(); CopyPathCommand.Refresh(); RevealFileCommand.Refresh(); }
    }
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
    public bool IsScanning { get => _scanning; private set { _scanning = value; Changed(); Changed(nameof(IsWorking)); CancelCommand.Refresh(); } }
    public bool CanEditScope => !IsBusy;
    public string? SelectedFolder { get => _selectedFolder; set { _selectedFolder = value; Changed(); RemoveFolderCommand.Refresh(); } }

    public MainViewModel(IDriveService drives, IIndexDatabase database, IFileIndexingService? indexing = null,
        IScanRepository? repository = null, Func<IReadOnlyList<string>>? pickFolders = null,
        Action<string>? copyPath = null, Action<string>? revealFile = null, Func<string?>? pickReferenceFile = null,
        IDuplicateAnalysisService? duplicates = null, IDuplicateRepository? duplicateRepository = null, Func<IndexedFile, bool>? validateDevice = null)
    {
        _drives = drives;
        _database = database;
        _repository = repository ?? database as IScanRepository;
        _indexing = indexing;
        _searchRepository = database as IFileSearchRepository;
        void Error(Exception ex) { Status = "작업 오류: " + ex.Message; }
        SearchCommand = new(SearchAsync, Error, () => !IsBusy && _ready && _searchRepository is not null);
        ResetSearchCommand = new(async () =>
        {
            NameFilter = ExtensionsFilter = MinimumSize = MaximumSize = "";
            CreatedFrom = CreatedTo = ModifiedFrom = ModifiedTo = null;
            SearchSelectedScope = SortDescending = false;
            SortIndex = 0;
            foreach (var property in new[] { nameof(NameFilter), nameof(ExtensionsFilter), nameof(MinimumSize), nameof(MaximumSize),
                nameof(CreatedFrom), nameof(CreatedTo), nameof(ModifiedFrom), nameof(ModifiedTo), nameof(SearchSelectedScope), nameof(SortDescending), nameof(SortIndex) }) Changed(property);
            await SearchAsync();
        }, Error, () => !IsBusy && _ready);
        CopyPathCommand = new(() => { try { copyPath?.Invoke(SelectedFile!.FullPath); Status = "파일 경로를 복사했습니다."; } catch (Exception ex) { Error(ex); } }, () => !IsBusy && SelectedFile is not null && copyPath is not null);
        RevealFileCommand = new(() => { try { if (validateDevice is not null && !validateDevice(SelectedFile!)) throw new IOException("원래 장치를 확인할 수 없습니다. 연결을 확인하고 해당 범위를 재검사하세요."); revealFile?.Invoke(SelectedFile!.FullPath); } catch (Exception ex) { Error(ex); } }, () => !IsBusy && SelectedFile is not null && revealFile is not null);
        ReferenceFileCommand = new(() =>
        {
            try
            {
                var path = pickReferenceFile?.Invoke();
                if (path is null) return;
                NameFilter = Path.GetFileName(path);
                ExtensionsFilter = Path.GetExtension(path);
                Changed(nameof(NameFilter)); Changed(nameof(ExtensionsFilter));
                Status = "기준 파일의 이름·확장자를 적용했습니다. 검색을 누르세요. 콘텐츠 중복 판정은 후속 기능입니다.";
            }
            catch (Exception ex) { Error(ex); }
        }, () => !IsBusy && pickReferenceFile is not null);
        RefreshCommand = new(InitializeAsync, Error, () => !IsBusy);
        ScanCommand = new(RunScanAsync, Error, () => !IsBusy && _ready && _indexing is not null && (Drives.Any(d => d.IsSelected) || Folders.Count > 0));
        PreviousPageCommand = new(() => ChangePageAsync(-1), Error, () => !IsBusy && _page > 0);
        NextPageCommand = new(() => ChangePageAsync(1), Error, () => !IsBusy && ((long)_page + 1) * PageSize < _total);
        CancelCommand = new(CancelScan, () => (IsScanning && _cancellation?.IsCancellationRequested == false)
            || (IsSearching && _queryCancellation?.IsCancellationRequested == false)
            || (IsAnalyzing && _duplicateCancellation?.IsCancellationRequested == false));
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
        ConfigureDuplicates(duplicates, duplicateRepository ?? database as IDuplicateRepository);
    }

    private void RefreshCommands()
    {
        RefreshCommand.Refresh(); ScanCommand.Refresh(); PreviousPageCommand.Refresh(); NextPageCommand.Refresh();
        AddFolderCommand.Refresh(); RemoveFolderCommand.Refresh(); SelectAllDrivesCommand.Refresh(); CancelCommand.Refresh();
        SearchCommand.Refresh(); ResetSearchCommand.Refresh(); CopyPathCommand.Refresh(); RevealFileCommand.Refresh(); ReferenceFileCommand.Refresh();
        RefreshDuplicateCommands();
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
            await LoadHistoryAsync();
            await LoadDuplicateGroupsAsync();
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
        catch (OperationCanceledException)
        {
            Status = "저장 인덱스 조회를 취소했습니다. 새로고침으로 다시 조회할 수 있습니다.";
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
            await LoadPageAsync(pageNumber: 0);
            await LoadHistoryAsync();
        }
        catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
        {
            Status = "검사를 취소했습니다. 기존 인덱스는 유지됩니다.";
        }
        catch (OperationCanceledException) { Status = "검사 후 결과 조회를 취소했습니다. 검사 기록과 인덱스는 저장되어 있습니다."; }
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
        if (IsScanning) _cancellation?.Cancel();
        _queryCancellation?.Cancel();
        _duplicateCancellation?.Cancel();
        CancelCommand.Refresh();
        Status = IsAnalyzing ? "중복 분석 취소 요청 · 현재 읽기/저장을 마치는 중입니다." : IsSearching ? "검색 취소 요청 · 조회를 중단하는 중입니다." : "취소 요청 · 현재 메타데이터 작업과 저장을 마치는 중입니다.";
    }

    public async Task CancelAndWaitAsync()
    {
        CancelScan();
        if (_scanTask is not null) { try { await _scanTask; } catch (Exception) { /* RunScanAsync displays the failure. */ } }
        if (_queryTask is not null) { try { await _queryTask; } catch (Exception) { /* Query owner displays the failure. */ } }
        if (_duplicateTask is not null) { try { await _duplicateTask; } catch (Exception) { /* Analysis owner displays the failure. */ } }
        if (_duplicateBrowseTask is not null) { try { await _duplicateBrowseTask; } catch (Exception) { /* Browse owner displays the failure. */ } }
    }

    private async Task ChangePageAsync(int direction)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await LoadPageAsync(pageNumber: Math.Max(0, _page + direction)); }
        catch (OperationCanceledException) { Status = "페이지 조회를 취소했습니다. 이전 결과를 유지합니다."; }
        finally { IsBusy = false; }
    }

    private async Task LoadPageAsync(FileSearch? requestedSearch = null, int? pageNumber = null)
    {
        var summary = await _database.GetSummaryAsync();
        IndexStatus = summary.FileCount == 0 ? "저장된 인덱스 없음 · 파일 0개" : $"인덱스 파일 {summary.FileCount:N0}개 (없음·접근 제한 상태 포함)";
        if (_repository is null) return;
        var query = requestedSearch ?? _search;
        var requestedPage = pageNumber ?? _page;
        _queryCancellation = new();
        var token = _queryCancellation.Token;
        IsSearching = true;
        var previousStatus = Status;
        Status = "저장 인덱스 검색 중 · 오래 걸리면 취소를 누르세요.";
        IndexPage page;
        try
        {
            _queryTask = _searchRepository is null ? _repository.GetPageAsync(requestedPage, PageSize, token)
                : _searchRepository.SearchAsync(query, requestedPage, PageSize, token);
            page = await _queryTask;
            token.ThrowIfCancellationRequested();
            Status = previousStatus;
        }
        finally
        {
            IsSearching = false;
            _queryTask = null;
            _queryCancellation.Dispose();
            _queryCancellation = null;
        }
        _search = query;
        _page = requestedPage;
        _total = page.TotalCount;
        Files.Clear();
        foreach (var file in page.Files) Files.Add(file);
        SelectedFile = null;
        PageText = _total == 0 ? "조건에 맞는 파일이 없습니다." : $"{_page + 1:N0} / {(_total + PageSize - 1) / PageSize:N0} 페이지 · 검색 결과 {_total:N0}개";
    }

    public async Task SearchAsync()
    {
        if (IsBusy || !_ready) return;
        var roots = SearchSelectedScope ? Drives.Where(d => d.IsSelected).Select(d => d.Snapshot.RootPath).Concat(Folders).ToArray() : [];
        if (SearchSelectedScope && roots.Length == 0) throw new ArgumentException("검색 범위로 사용할 드라이브 또는 폴더를 선택하세요.");
        static long? Bytes(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            if (!decimal.TryParse(input, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value < 0 || value > long.MaxValue / 1048576m)
                throw new ArgumentException("크기는 0 이상의 MiB 숫자로 입력하세요.");
            return checked((long)(value * 1048576m));
        }
        static DateTimeOffset? Date(DateTime? value, bool end = false) => value is { } date
            ? new DateTimeOffset(DateTime.SpecifyKind(end ? date.Date.AddDays(1) : date.Date, DateTimeKind.Local)) : null;
        var search = new FileSearch
        {
            Name = NameFilter.Trim(), Extensions = ExtensionsFilter.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Roots = roots, MinimumBytes = Bytes(MinimumSize), MaximumBytes = Bytes(MaximumSize),
            CreatedFrom = Date(CreatedFrom), CreatedBefore = Date(CreatedTo, true), ModifiedFrom = Date(ModifiedFrom), ModifiedBefore = Date(ModifiedTo, true),
            Sort = (FileSort)SortIndex, Descending = SortDescending
        };
        search.Validate();
        IsBusy = true;
        try { await LoadPageAsync(search, 0); SelectedTab = 1; Status = "저장 인덱스 검색 완료 · 모든 입력 조건을 함께 적용했습니다."; }
        catch (OperationCanceledException) { Status = "검색을 취소했습니다. 이전 결과와 저장된 인덱스를 유지합니다."; }
        finally { IsBusy = false; }
    }

    private async Task LoadHistoryAsync()
    {
        if (_searchRepository is null || _repository is null) return;
        var history = await _searchRepository.GetLatestScanAsync();
        Issues.Clear();
        if (history is null) { ProgressText = "저장된 검사 이력 없음"; return; }
        var status = history.Status switch { "Completed" => "완료", "CompletedWithIssues" => "오류/건너뜀 포함 완료", "Cancelled" => "취소", "Interrupted" => "중단", "Running" => "검사 중", _ => "실패" };
        var time = DateTimeOffset.Parse(history.StartedUtc, CultureInfo.InvariantCulture).ToLocalTime();
        ProgressText = $"마지막 검사 #{history.ScanId} · {time:g} · {status}\n파일 {history.Files:N0}개 · 오류 {history.Errors:N0}개 · 건너뜀 {history.Skipped:N0}개";
        foreach (var issue in await _repository.GetIssuesAsync(history.ScanId)) Issues.Add(issue);
    }
}
