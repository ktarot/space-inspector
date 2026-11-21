using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using SpaceInspector.Core.Models;
namespace SpaceInspector.App.Views;

public sealed class BatchFileChangeDialog : Window
{
    public IReadOnlyList<FileChangeRequest>? Requests { get; private set; }
    public BatchFileChangeDialog(IReadOnlyList<IndexedFile> files, bool renameOnly)
    {
        Title = renameOnly ? "선택 파일 이름 일괄 변경" : "선택 파일 일괄 이동";
        Width = 940; Height = 580; MinWidth = 720; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(18) }; Content = layout;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        top.Children.Add(new TextBlock { Text = $"선택 {files.Count}개 · 현재 페이지 · 경로 순서로 번호 부여 · 확장자 유지", Margin = new Thickness(0, 0, 0, 10) });
        var row = new WrapPanel(); top.Children.Add(row);
        var mode = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 8, 8), ItemsSource = new[] { "이름 앞에 붙이기", "공통 이름 + 번호" }, SelectedIndex = 0 };
        var name = new TextBox { Width = 220, Margin = new Thickness(0, 0, 8, 8), MaxLength = 180, ToolTip = "접두어 또는 공통 이름" };
        var start = new TextBox { Text = "1", Width = 70, Margin = new Thickness(0, 0, 8, 8), ToolTip = "시작 번호 (1~999999)" };
        var folder = new TextBox { Text = Path.GetDirectoryName(files[0].FullPath)!, IsReadOnly = true, Width = 650, Margin = new Thickness(0, 0, 8, 8) };
        var browse = new Button { Content = "대상 폴더 선택", Margin = new Thickness(0, 0, 0, 8) };
        if (renameOnly)
        {
            row.Children.Add(mode); row.Children.Add(name); row.Children.Add(new TextBlock { Text = "시작 번호", Margin = new Thickness(0, 5, 8, 0) }); row.Children.Add(start);
            top.Children.Add(new TextBlock { Text = "예: 여행_ + 001.jpg · 각 파일의 원래 폴더를 유지합니다.", Margin = new Thickness(0, 0, 0, 8) });
        }
        else { row.Children.Add(folder); row.Children.Add(browse); }
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        top.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = "취소", IsCancel = true, Margin = new Thickness(4), Padding = new Thickness(16, 8, 16, 8) });
        var accept = new Button { Content = "사전 검사 후 최종 확인", IsDefault = true, Margin = new Thickness(4), Padding = new Thickness(16, 8, 16, 8) }; buttons.Children.Add(accept);
        var grid = CreateGrid(); layout.Children.Add(grid);
        void Update()
        {
            Requests = null; accept.IsEnabled = false; start.IsEnabled = mode.SelectedIndex == 1;
            try
            {
                var requests = BuildRequests(files, renameOnly, folder.Text, name.Text, mode.SelectedIndex == 1, start.Text);
                Requests = requests;
                grid.ItemsSource = requests.Select(r => new Row(r.File.FullPath, r.TargetPath, "사전 검사 전"));
                error.Foreground = System.Windows.Media.Brushes.DimGray;
                error.Text = "같은 이름은 덮어쓰지 않습니다. 실행 전 장치와 모든 변경 경로를 검사합니다."; accept.IsEnabled = true;
            }
            catch (Exception ex) { error.Foreground = System.Windows.Media.Brushes.DarkRed; grid.ItemsSource = null; error.Text = ex.Message; }
        }
        name.TextChanged += (_, _) => Update(); start.TextChanged += (_, _) => Update(); mode.SelectionChanged += (_, _) => Update();
        browse.Click += (_, _) => {
            var picker = new OpenFolderDialog { Title = "이동할 폴더", InitialDirectory = folder.Text };
            if (picker.ShowDialog(this) == true) { folder.Text = picker.FolderName; Update(); }
        };
        accept.Click += (_, _) => { if (Requests is not null) DialogResult = true; }; Update();
    }
    internal static IReadOnlyList<FileChangeRequest> BuildRequests(IReadOnlyList<IndexedFile> files, bool rename, string folder, string name, bool sequence, string start)
    {
        if (rename && (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) throw new ArgumentException("접두어 또는 공통 이름을 입력하세요. 경로 문자는 사용할 수 없습니다.");
        int number = 1;
        if (rename && sequence && (!int.TryParse(start, out number) || number is < 1 or > 999999)) throw new ArgumentException("시작 번호는 1~999999로 입력하세요.");
        return files.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).Select((file, index) => new FileChangeRequest(file,
            Path.Combine(rename ? Path.GetDirectoryName(file.FullPath)! : folder,
                rename ? name + (sequence ? (number + index).ToString("D3") + Path.GetExtension(file.FileName) : file.FileName) : file.FileName))).ToArray();
    }
    private sealed record Row(string Source, string Target, string Status);
    private static DataGrid CreateGrid()
    {
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, Margin = new Thickness(0, 8, 0, 8), EnableRowVirtualization = true };
        void Column(string title, string field, double width, bool stretch)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(4)));
            style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(field)));
            grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(field), MinWidth = stretch ? 250 : 220,
                Width = new DataGridLength(width, stretch ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel), ElementStyle = style });
        }
        Column("현재 경로", "Source", 1, true); Column("변경 경로", "Target", 1, true); Column("처리", "Status", 240, false);
        return grid;
    }
    public static bool Confirm(Window owner, IReadOnlyList<FileChangePlan> plans) => ShowTable(owner, plans.All(p => p.UndoOf is not null) ? "일괄 되돌리기 최종 확인" : "일괄 변경 최종 확인",
        (plans.All(p => p.UndoOf is not null) ? "선택한 파일의 마지막 변경을 되돌립니다. 복원 후 중복 판정은 다시 검사하세요.\n" : "") +
        $"{plans.Count}개 변경 · 다른 볼륨 {plans.Count(p => p.IsCrossVolume)}개 · 작업 범위 밖 {plans.Count(p => !p.RemainsInScope)}개\n"
        + "다른 볼륨은 복사본 검증 후 원본을 제거하며 대상 폴더의 권한을 상속합니다. 보관 지정은 새 경로로 이어집니다.\n"
        + "중단/오류 시 완료된 파일은 유지하고 나머지는 멈춥니다. 이미 완료된 처리를 자동으로 취소하지 않습니다.",
        plans.Select(p => new Row(p.Source.FullPath, p.TargetPath, (p.IsCrossVolume ? "복사·검증·원본 제거" : "경로 변경") + (p.RemainsInScope ? " · 범위 안" : " · 범위 밖"))), true, plans.All(p => p.UndoOf is not null) ? "되돌리기" : "변경");
    public static void ShowResults(Window owner, IReadOnlyList<FileChangeOutcome> outcomes) => ShowTable(owner, outcomes.All(o => o.Plan.UndoOf is not null) ? "일괄 되돌리기 결과" : "일괄 변경 결과",
        "완료된 변경은 유지됩니다. 실패한 파일은 ‘파일 변경 기록 확인’으로 확인하세요.\n파일별 결과는 작업 폴더의 file-actions/reports에도 저장했습니다.",
        outcomes.Select(o => new Row(o.Plan.Source.FullPath, o.Plan.TargetPath,
            (o.State switch { "Completed" => "완료", "Cancelled" => "중단", "Failed" => "실패", _ => "미처리" }) + (o.Error is null ? "" : " · " + o.Error))), false);
    private static bool ShowTable(Window owner, string title, string text, IEnumerable<Row> rows, bool confirm, string operation = "변경")
    {
        var window = new Window { Owner = owner, Title = title, Width = 1000, Height = 560, MinWidth = 720, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var layout = new DockPanel { Margin = new Thickness(18) }; window.Content = layout;
        var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }; DockPanel.SetDock(hint, Dock.Top); layout.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        buttons.Children.Add(new Button { Content = confirm ? "취소" : "닫기", IsCancel = true, Margin = new Thickness(4), Padding = new Thickness(18, 8, 18, 8) });
        if (confirm)
        {
            var execute = new Button { Content = $"{rows.Count()}개 {operation} 실행", Margin = new Thickness(4), Padding = new Thickness(18, 8, 18, 8) };
            execute.Click += (_, _) => window.DialogResult = true; buttons.Children.Add(execute);
        }
        var grid = CreateGrid(); grid.ItemsSource = rows.ToArray(); layout.Children.Add(grid);
        return window.ShowDialog() == true;
    }
}
