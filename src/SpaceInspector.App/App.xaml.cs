using System.Windows;
using SpaceInspector.App.ViewModels;
using SpaceInspector.App.Views;
using SpaceInspector.Infrastructure.Database;
using SpaceInspector.Infrastructure.FileSystem;
using SpaceInspector.Core.Indexing;
using SpaceInspector.Core.DuplicateDetection;
using Microsoft.Win32;

namespace SpaceInspector.App;

public partial class App : Application
{
    private System.IO.FileStream? _instanceLock;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var databasePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpaceInspector", "index.db");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);
            _instanceLock = new System.IO.FileStream(databasePath + ".lock", System.IO.FileMode.OpenOrCreate,
                System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("앱이 이미 실행 중이거나 데이터 폴더를 열 수 없습니다.\n" + ex.Message, "Space Inspector");
            Shutdown();
            return;
        }
        var volumes = new WindowsVolumeIdentityService();
        var database = new SqliteIndexDatabase(databasePath, volumes);
        var source = new MetadataOnlyFileSource([System.IO.Path.GetDirectoryName(databasePath)!, AppContext.BaseDirectory], volumes);
        var viewModel = new MainViewModel(new LocalDriveService(), database, new FileIndexingService(source, database), database, () =>
        {
            var dialog = new OpenFolderDialog { Title = "검사할 폴더 선택", Multiselect = true };
            return dialog.ShowDialog(MainWindow) == true ? dialog.FolderNames : [];
        }, copyPath: Clipboard.SetText, revealFile: path =>
        {
            if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("원본 파일이 없거나 접근할 수 없습니다. 드라이브 연결과 권한을 확인하세요.");
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add("/select," + path);
            System.Diagnostics.Process.Start(start);
        }, pickReferenceFile: () =>
        {
            var dialog = new OpenFileDialog { Title = "기준 파일 선택 (이름·확장자 적용)", CheckFileExists = true };
            return dialog.ShowDialog(MainWindow) == true ? dialog.FileName : null;
        }, duplicates: new DuplicateAnalysisService(database, new WindowsFileFingerprintService(volumes)), duplicateRepository: database,
            validateDevice: file => file.VolumeIdentifier is not null && volumes.GetIdentity(file.FullPath) == file.VolumeIdentifier);
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
