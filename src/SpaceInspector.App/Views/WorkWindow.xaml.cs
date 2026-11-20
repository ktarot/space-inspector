using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Diagnostics;
using System.IO;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.FileSystem;
using SpaceInspector.App.ViewModels;

namespace SpaceInspector.App.Views;

public partial class WorkWindow : Window
{
    private bool _closing;
    private bool _closeReady;
    public WorkWindow(WorkViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler(OpenRowMenu));
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        Closing += async (_, e) =>
        {
            if (_closeReady) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            await viewModel.StopAsync();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            viewModel.Dispose(); _closeReady = true; Close();
        };
    }

    private void OpenRowMenu(object sender, ContextMenuEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject);
        if (row is null || DataContext is not WorkViewModel vm) { e.Handled = true; return; }
        row.IsSelected = true;
        var menu = new ContextMenu();
        void Action(string title, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled };
            item.Click += (_, _) => { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "작업 실패"); } };
            menu.Items.Add(item);
        }
        void Command(string title, ICommand command, bool enabled = true)
        {
            var item = new MenuItem { Header = title, Command = command, IsEnabled = enabled };
            menu.Items.Add(item);
        }
        string? path = null, volume = null;
        if (row.Item is IndexedFile file) { vm.SelectedFile = file; path = file.FullPath; volume = file.VolumeIdentifier; }
        else if (row.Item is DuplicateMember member) { vm.SelectedMember = member; path = member.FullPath; volume = member.VolumeIdentifier; }
        else if (row.Item is DuplicateGroup group)
        {
            if (vm.SelectedGroup != group) vm.SelectedGroup = group;
            Command("그룹 파일 보기", vm.LoadMembersCommand);
            Action("그룹 내 파일 경로 복사", () => _ = vm.CopyGroupPathsAsync(), vm.CanConfigure);
        }
        else if (row.Item is PendingItem pending)
        {
            vm.SelectedPending = pending;
            bool retryable = pending.State == "응답 대기";
            Command("해당 항목 재시도", vm.RetryCommand, retryable);
            Command("관리자 권한으로 다시 확인", vm.PermissionCommand, retryable);
            Command("이번 작업에서 건너뛰기", vm.SkipCommand, retryable);
            menu.Items.Add(new Separator());
            Action("경로 복사", () => Clipboard.SetText(pending.Path));
            Action("전체 이유 보기", () => MessageBox.Show(this, pending.Path + "\n\n" + pending.Reason, "응답 대기 이유"));
            Action("상위 폴더 열기", () => Process.Start(new ProcessStartInfo(Path.GetDirectoryName(pending.Path)!) { UseShellExecute = true }));
        }
        if (path is not null)
        {
            void Validate()
            {
                if (volume is null || new WindowsVolumeIdentityService().GetIdentity(path) != volume || !File.Exists(path))
                    throw new IOException("원래 장치/파일을 확인할 수 없습니다.");
            }
            Action("파일 열기", () => { Validate(); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }, vm.CanConfigure);
            Action("파일 정보 보기", () => {
                Validate(); var info = new FileInfo(path);
                MessageBox.Show(this, $"{path}\n\n크기: {info.Length:N0} 바이트\n수정: {info.LastWriteTime}\n속성: {info.Attributes}\n보관 지정: {(vm.IsKept(path) ? "예" : "아니요")}", "파일 정보");
            }, vm.CanConfigure);
            Command("경로 복사", vm.CopyCommand);
            Command("포함 폴더 열기", vm.RevealCommand);
            if (row.Item is DuplicateMember)
            {
                menu.Items.Add(new Separator());
                Action(vm.IsKept(path) ? "보관 지정 해제" : "보관할 파일로 지정", () => vm.ToggleKeep(path), vm.CanConfigure);
                Command("선택 중복 사본을 휴지통으로 이동", vm.RecycleDuplicateCommand);
            }
        }
        e.Handled = true;
        if (menu.Items.Count > 0) { menu.PlacementTarget = row; menu.IsOpen = true; }
    }
    private static DataGridRow? FindRow(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is DataGridRow row) return row;
            current = current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
