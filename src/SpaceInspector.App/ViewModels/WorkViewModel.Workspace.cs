using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
namespace SpaceInspector.App.ViewModels;

public sealed record SavedView(string Label, string Name, int Media, string Minimum, string Maximum,
    int Comparison, bool Kept, int Sort, bool Descending)
{
    public override string ToString() => Label;
}

public sealed partial class WorkViewModel
{
    public int ComparisonFilter { get; set; }
    public bool KeptOnly { get; set; }
    public int SortIndex { get; set; }
    public bool SortDescending { get; set; }
    public string? GroupFilter { get; private set; }
    public string GroupFilterText => GroupFilter is null ? "현재 작업 전체에서 검색" : "선택 파일과 같은 중복 그룹 · 초기화로 해제";
    public string ViewName { get; set; } = "";
    public ObservableCollection<SavedView> SavedViews { get; } = [];
    public SavedView? SelectedView { get; set; }
    public RelayCommand SaveViewCommand { get; private set; } = null!;
    public AsyncCommand ApplyViewCommand { get; private set; } = null!;
    public RelayCommand DeleteViewCommand { get; private set; } = null!;
    public AsyncCommand ResetFiltersCommand { get; private set; } = null!;
    public RelayCommand KeepSelectedCommand { get; private set; } = null!;
    public AsyncCommand ShowSameGroupCommand { get; private set; } = null!;
    private string ViewsFile => Path.Combine(_jobs, "saved-views.json");
    private void InitializeWorkspaceCommands()
    {
        void Error(Exception ex) => Status = "보기 설정 오류 · " + ex.Message;
        SaveViewCommand = new(() => {
            try {
                CaptureSearch();
                var label = ViewName.Trim();
                if (label.Length is 0 or > 80) throw new ArgumentException("보기 이름을 1~80자로 입력하세요.");
                var view = new SavedView(label, NameFilter, MediaFilter, MinimumSizeMiB, MaximumSizeMiB, ComparisonFilter, KeptOnly, SortIndex, SortDescending);
                var views = SavedViews.Where(v => !string.Equals(v.Label, label, StringComparison.OrdinalIgnoreCase)).Append(view).ToArray();
                PersistViews(views); SelectedView = view; Changed(nameof(SelectedView));
                Status = "조건을 저장했습니다 · " + label;
            } catch (Exception ex) { Error(ex); }
        }, () => CanConfigure);
        ApplyViewCommand = new(async () => {
            if (SelectedView is not { } view) return;
            NameFilter = view.Name; MediaFilter = view.Media; MinimumSizeMiB = view.Minimum; MaximumSizeMiB = view.Maximum;
            ComparisonFilter = view.Comparison; KeptOnly = view.Kept; SortIndex = view.Sort; SortDescending = view.Descending;
            GroupFilter = null; NotifyFilters(); _filePage = 0; await BrowseAsync();
        }, Error, () => CanConfigure && _session is not null);
        DeleteViewCommand = new(() => {
            try { if (SelectedView is { } view) { PersistViews(SavedViews.Where(v => v != view).ToArray()); SelectedView = null; Changed(nameof(SelectedView)); } }
            catch (Exception ex) { Error(ex); }
        }, () => CanConfigure);
        ResetFiltersCommand = new(async () => {
            NameFilter = MinimumSizeMiB = MaximumSizeMiB = ""; MediaFilter = ComparisonFilter = SortIndex = 0;
            KeptOnly = SortDescending = false; GroupFilter = null; NotifyFilters(); _filePage = 0; await BrowseAsync();
        }, Error, () => CanConfigure && _session is not null);
        KeepSelectedCommand = new(() => {
            var path = SelectedFile?.FullPath ?? SelectedMember?.FullPath;
            if (path is not null) ToggleKeep(path);
        }, () => CanConfigure && (SelectedFile is not null || SelectedMember is not null));
        ShowSameGroupCommand = new(async () => {
            if (SelectedFile?.DuplicateGroupId is not { } group) return;
            GroupFilter = group; NameFilter = MinimumSizeMiB = MaximumSizeMiB = "";
            MediaFilter = ComparisonFilter = 0; KeptOnly = false;
            NotifyFilters(); _filePage = 0; await BrowseAsync();
        }, Error, () => CanConfigure && SelectedFile?.DuplicateGroupId is not null);
    }
    private void NotifyFilters()
    {
        foreach (var name in new[] { nameof(NameFilter), nameof(MediaFilter), nameof(MinimumSizeMiB), nameof(MaximumSizeMiB),
            nameof(ComparisonFilter), nameof(KeptOnly), nameof(SortIndex), nameof(SortDescending), nameof(GroupFilterText) }) Changed(name);
    }
    private void PersistViews(SavedView[] views)
    {
        Directory.CreateDirectory(_jobs);
        File.WriteAllText(ViewsFile + ".tmp", JsonSerializer.Serialize(views));
        File.Move(ViewsFile + ".tmp", ViewsFile, true);
        SavedViews.Clear(); foreach (var view in views) SavedViews.Add(view);
    }
    private void LoadSavedViews()
    {
        if (!File.Exists(ViewsFile)) return;
        var views = JsonSerializer.Deserialize<SavedView[]>(File.ReadAllText(ViewsFile)) ?? [];
        SavedViews.Clear(); foreach (var view in views) SavedViews.Add(view);
    }
}
