using System.Collections.ObjectModel;
using SpaceInspector.Core.Interfaces;

namespace SpaceInspector.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IDriveService _drives;
    private readonly IIndexDatabase _database;
    private string _status = "초기화 준비 중";
    private string _indexStatus = "인덱스 확인 중";
    private bool _busy;
    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public AsyncCommand RefreshCommand { get; }
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string IndexStatus { get => _indexStatus; private set { _indexStatus = value; Changed(); } }
    public bool IsBusy { get => _busy; private set { _busy = value; Changed(); } }

    public MainViewModel(IDriveService drives, IIndexDatabase database)
    {
        _drives = drives;
        _database = database;
        RefreshCommand = new AsyncCommand(InitializeAsync, _ => Status = "정보를 불러오지 못했습니다. 새로고침으로 다시 시도하세요.");
    }

    public async Task InitializeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "로컬 드라이브와 인덱스를 확인하고 있습니다.";
        try
        {
            await _database.InitializeAsync();
            var summary = await _database.GetSummaryAsync();
            IndexStatus = summary.FileCount == 0 ? "저장된 인덱스 없음 · 파일 0개" : $"인덱스 파일 {summary.FileCount:N0}개";
            var snapshots = await _drives.GetLocalDrivesAsync();
            var selected = Drives.Where(d => d.IsSelected).Select(d => d.Snapshot.RootPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Drives.Clear();
            foreach (var snapshot in snapshots)
                Drives.Add(new(snapshot) { IsSelected = snapshot.IsReady && selected.Contains(snapshot.RootPath) });
            Status = Drives.Count == 0 ? "사용 가능한 로컬 드라이브가 없습니다." : "준비 완료 · 파일 검사는 Phase 2에서 제공됩니다.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            IndexStatus = "정보 확인 실패";
            Status = "초기화 오류: " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
