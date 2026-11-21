using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SpaceInspector.Core.Models;
namespace SpaceInspector.App.Views;

public sealed class FileChangeDialog : Window
{
    private readonly TextBox _folder = new() { IsReadOnly = true, Margin = new Thickness(0, 4, 8, 4) };
    private readonly TextBox _name = new() { Margin = new Thickness(0, 4, 0, 10), MaxLength = 255 };
    private readonly TextBlock _target = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkRed };
    public string TargetPath => Path.Combine(_folder.Text, _name.Text);
    public FileChangeDialog(IndexedFile source, bool renameOnly)
    {
        Title = renameOnly ? "파일 이름 변경" : "파일 이동";
        Width = 680; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "현재 파일", FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = source.FullPath, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "변경할 폴더 · 다른 볼륨은 복사본 검증 후 원본 제거" });
        var row = new DockPanel(); panel.Children.Add(row);
        var browse = new Button { Content = "폴더 선택", Padding = new Thickness(10, 5, 10, 5), IsEnabled = !renameOnly };
        DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(_folder);
        panel.Children.Add(new TextBlock { Text = "파일 이름 (확장자 포함)", Margin = new Thickness(0, 10, 0, 0) }); panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text = "변경 후 경로", FontWeight = FontWeights.Bold }); panel.Children.Add(_target); panel.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "취소", IsCancel = true, Margin = new Thickness(4), Padding = new Thickness(16, 7, 16, 7) };
        var accept = new Button { Content = "변경 내용 확인", IsDefault = true, Margin = new Thickness(4), Padding = new Thickness(16, 7, 16, 7) };
        buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons);
        _folder.Text = Path.GetDirectoryName(source.FullPath)!; _name.Text = source.FileName;
        void Update() { try { _target.Text = TargetPath; } catch { _target.Text = "올바른 파일 이름을 입력하세요."; } }
        _name.TextChanged += (_, _) => Update(); _folder.TextChanged += (_, _) => Update(); Update();
        browse.Click += (_, _) => {
            var picker = new OpenFolderDialog { Title = "이동할 폴더", InitialDirectory = _folder.Text };
            if (picker.ShowDialog(this) == true) _folder.Text = picker.FolderName;
        };
        accept.Click += (_, _) => {
            if (string.IsNullOrWhiteSpace(_name.Text) || _name.Text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            { _error.Text = "파일 이름에 경로나 사용할 수 없는 문자가 있습니다."; return; }
            DialogResult = true;
        };
    }
}
