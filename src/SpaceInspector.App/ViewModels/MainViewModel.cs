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
                "CompletedWithIssues" => "검사 완료 · 오류/건너뜀 경로를 확인하세요…16002 tokens truncated…tus='Running';";
        if (Convert.ToInt64(command.ExecuteScalar()) > 0) throw new InvalidOperationException("진행 중인 검사가 있습니다.");
        command.CommandText = "INSERT INTO ScanRuns(StartedUtc,Status) VALUES($now,'Running'); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var id = Convert.ToInt64(command.ExecuteScalar());
        foreach (var root in roots)
        {
            var volume = _volumes?.GetIdentity(root);
            if (volume is not null) RegisterVolume(command, volume, Path.GetPathRoot(root)!);
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO ScanScopes(ScanId,RootPath,PathPrefix,VolumeIdentifier) VALUES($id,$root,$prefix,$volume);";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$root", root);
            command.Parameters.AddWithValue("$prefix", ScanScope.Prefix(root));
            command.Parameters.AddWithValue("$volume", (object?)volume ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return id;
    }, cancellationToken);

    public Task AppendAsync(long scanId, IReadOnlyList<ScanObservation> batch, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        foreach (var observation in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", scanId);
            if (observation.File is { } file)
            {
                if (file.VolumeIdentifier is not null)
                {
                    command.CommandText = "SELECT COUNT(*) FROM ScanScopes WHERE ScanId=$id AND VolumeIdentifier=$volume";
                    command.Parameters.AddWithValue("$volume", file.VolumeIdentifier);
                    if (Convert.ToInt64(command.ExecuteScalar()) == 0) throw new IOException("검사 시작 이후 장치 식별자가 달라졌습니다. 다시 검사하세요.");
                    command.Parameters.RemoveAt("$volume");
                }
                var key = file.VolumeIdentifier is null ? "path|" + file.FullPath : "volume|" + file.VolumeIdentifier + "|" + file.VolumeRelativePath;
                if (file.VolumeIdentifier is not null)
                {
                    command.CommandText = "UPDATE Files SET IndexKey=$key WHERE VolumeIdentifier IS NULL AND FullPath=$path COLLATE NOCASE AND NOT EXISTS(SELECT 1 FROM Files WHERE IndexKey=$key);";
                    command.Parameters.AddWithValue("$key", key);
                    command.Parameters.AddWithValue("$path", file.FullPath);
                    command.ExecuteNonQuery();
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("$id", scanId);
                }
                command.CommandText = """
                    INSERT INTO Files(FullPath,FileName,Extension,ParentDirectory,Drive,FileSize,CreationTimeUtc,
                        ModifiedTimeUtc,Attributes,IsCloudPlaceholder,ScanTimeUtc,ScanStatus,LastSeenScanId,IndexKey,VolumeIdentifier,VolumeRelativePath)
                    VALUES($path,$name,$ext,$parent,$drive,$size,$created,$modified,$attrs,$cloud,$now,'Normal',$id,$key,$volume,$relative)
                    ON CONFLICT(IndexKey) DO UPDATE SET FullPath=excluded.FullPath,FileName=excluded.FileName,Extension=excluded.Extension,
                        ParentDirectory=excluded.ParentDirectory,Drive=excluded.Drive,FileSize=excluded.FileSize,
                        CreationTimeUtc=excluded.CreationTimeUtc,ModifiedTimeUtc=excluded.ModifiedTimeUtc,
                        Attributes=excluded.Attributes,IsCloudPlaceholder=excluded.IsCloudPlaceholder,
                        ScanTimeUtc=excluded.ScanTimeUtc,ScanStatus='Normal',ScanError=NULL,LastSeenScanId=$id,
                        VolumeIdentifier=excluded.VolumeIdentifier,VolumeRelativePath=excluded.VolumeRelativePath,
                        FileSystemIdentifier=NULL,DuplicateRunId=NULL,DuplicateError=NULL,
                        PartialHash=NULL,FullHash=NULL,DuplicateGroupId=NULL,DuplicateStatus='Unverified';
                    """;
                command.Parameters.AddWithValue("$path", file.FullPath);
                command.Parameters.AddWithValue("$name", file.FileName);
                command.Parameters.AddWithValue("$ext", file.Extension);
                command.Parameters.AddWithValue("$parent", file.ParentDirectory);
                command.Parameters.AddWithValue("$drive", file.Drive);
                command.Parameters.AddWithValue("$size", file.FileSize);
                command.Parameters.AddWithValue("$created", file.CreationTimeUtc.ToString("O"));
                command.Parameters.AddWithValue("$modified", file.ModifiedTimeUtc.ToString("O"));
                command.Parameters.AddWithValue("$attrs", file.Attributes);
                command.Parameters.AddWithValue("$cloud", file.IsCloudPlaceholder is { } cloud ? (cloud ? 1 : 0) : DBNull.Value);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$volume", (object?)file.VolumeIdentifier ?? DBNull.Value);
                command.Parameters.AddWithValue("$relative", (object?)file.VolumeRelativePath ?? DBNull.Value);
            }
            else if (observation.Issue is { } issue)
            {
                command.CommandText = "INSERT INTO ScanIssues(ScanId,Path,PathPrefix,Status,Message) VALUES($id,$path,$prefix,$status,$message);";
                command.Parameters.AddWithValue("$path", issue.Path);
                command.Parameters.AddWithValue("$prefix", ScanScope.Prefix(issue.Path));
                command.Parameters.AddWithValue("$status", issue.Status);
                command.Parameters.AddWithValue("$message", issue.Message);
            }
            else continue;
            command.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }, cancellationToken);

    public Task FinishScanAsync(ScanResult result, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", result.ScanId);
        // A successful walk can reconcile only within selected scopes, excluding every inaccessible/skipped subtree.
        // substr + NOCASE avoids LIKE wildcard surprises in folder names such as "100%_done".
        if (result.Status is "Completed" or "CompletedWithIssues")
        {
            command.CommandText = """
                UPDATE Files SET ScanStatus='Missing', PartialHash=NULL,FullHash=NULL,DuplicateGroupId=NULL,DuplicateStatus='Unverified'
                WHERE (LastSeenScanId IS NULL OR LastSeenScanId <> $id)
                AND EXISTS (SELECT 1 FROM ScanScopes s WHERE s.ScanId=$id
                    AND (Files.VolumeIdentifier=s.VolumeIdentifier OR (Files.VolumeIdentifier IS NULL AND s.VolumeIdentifier IS NULL))
                    AND substr(Files.FullPath,1,length(s.PathPrefix))=s.PathPrefix COLLATE NOCASE)
                AND NOT EXISTS (SELECT 1 FROM ScanIssues i WHERE i.ScanId=$id
                    AND (Files.FullPath=i.Path COLLATE NOCASE OR substr(Files.FullPath,1,length(i.PathPrefix))=i.PathPrefix COLLATE NOCASE));
                """;
            command.ExecuteNonQuery();
        }
        command.CommandText = """
            UPDATE Files SET ScanStatus='NeedsRescan',DuplicateStatus='Unverified',PartialHash=NULL,FullHash=NULL,DuplicateGroupId=NULL
                WHERE EXISTS (SELECT 1 FROM ScanIssues i WHERE i.ScanId=$id
                AND EXISTS(SELECT 1 FROM ScanScopes s WHERE s.ScanId=$id AND (s.VolumeIdentifier=Files.VolumeIdentifier OR s.VolumeIdentifier IS NULL))
                AND (Files.FullPath=i.Path COLLATE NOCASE OR substr(Files.FullPath,1,length(i.PathPrefix))=i.PathPrefix COLLATE NOCASE));
            UPDATE Files SET ScanStatus='AccessDenied' WHERE EXISTS (SELECT 1 FROM ScanIssues i
                WHERE i.ScanId=$id AND i.Status='AccessDenied'
                AND EXISTS(SELECT 1 FROM ScanScopes s WHERE s.ScanId=$id AND (s.VolumeIdentifier=Files.VolumeIdentifier OR s.VolumeIdentifier IS NULL))
                AND (Files.FullPath=i.Path COLLATE NOCASE OR substr(Files.FullPath,1,length(i.PathPrefix))=i.PathPrefix COLLATE NOCASE));
            UPDATE Files SET ScanStatus='DeviceUnavailable' WHERE EXISTS (SELECT 1 FROM ScanIssues i
                WHERE i.ScanId=$id AND i.Status='Unavailable'
                AND EXISTS(SELECT 1 FROM ScanScopes s WHERE s.ScanId=$id AND (s.VolumeIdentifier=Files.VolumeIdentifier OR s.VolumeIdentifier IS NULL))
                AND (Files.FullPath=i.Path COLLATE NOCASE OR substr(Files.FullPath,1,length(i.PathPrefix))=i.PathPrefix COLLATE NOCASE));
            UPDATE ScanRuns SET FinishedUtc=$now,Status=$status,FileCount=$files,ErrorCount=$errors,SkippedCount=$skipped WHERE ScanId=$id;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", result.Status);
        command.Parameters.AddWithValue("$files", result.Files);
        command.Parameters.AddWithValue("$errors", result.Errors);
        command.Parameters.AddWithValue("$skipped", result.Skipped);
        command.ExecuteNonQuery();
        transaction.Commit();
    }, cancellationToken);

    public Task<IndexPage> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageNumber);
        if (pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM Files;";
        var count = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "SELECT FileId,FileName,FullPath,FileSize,ModifiedTimeUtc,ScanStatus,IsCloudPlaceholder,VolumeIdentifier FROM Files ORDER BY FileId LIMIT $limit OFFSET $offset;";
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)pageNumber * pageSize);
        using var reader = command.ExecuteReader();
        var files = new List<IndexedFile>(pageSize);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetBoolean(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return new IndexPage(files, count, pageNumber, pageSize);
    }, cancellationToken);

    public Task<IReadOnlyList<ScanIssue>> GetIssuesAsync(long scanId, int limit = 100, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<ScanIssue>>(() =>
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Path,Status,Message FROM ScanIssues WHERE ScanId=$id ORDER BY IssueId LIMIT $limit;";
        command.Parameters.AddWithValue("$id", scanId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var issues = new List<ScanIssue>();
        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); issues.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2))); }
        return issues;
    }, cancellationToken);

    private static void RegisterVolume(SqliteCommand command, string identity, string root)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$volume", identity);
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.CommandText = """
            UPDATE Files SET ScanStatus='DeviceUnavailable',DuplicateStatus='Unverified',FullHash=NULL,PartialHash=NULL,DuplicateGroupId=NULL
                WHERE Drive=$root COLLATE NOCASE AND VolumeIdentifier IS NOT NULL AND VolumeIdentifier<>$volume;
            UPDATE Files SET FullPath=$root || VolumeRelativePath,
                ParentDirectory=$root || substr(VolumeRelativePath,1,length(VolumeRelativePath)-length(FileName)-1),
                Drive=$root,ScanStatus='NeedsRescan',DuplicateStatus='Unverified',FullHash=NULL,PartialHash=NULL,DuplicateGroupId=NULL
                WHERE VolumeIdentifier=$volume AND Drive<>$root COLLATE NOCASE AND VolumeRelativePath IS NOT NULL;
            INSERT INTO Volumes VALUES($volume,$root,$now) ON CONFLICT(VolumeIdentifier) DO UPDATE SET RootPath=$root,LastSeenUtc=$now;
            """;
        command.ExecuteNonQuery();
    }
}
