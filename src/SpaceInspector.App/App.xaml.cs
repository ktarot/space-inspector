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
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 3 && e.Args[0] == "--permission-worker" && int.TryParse(e.Args[2], out var parent))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { await PermissionWorker.RunAsync(e.Args[1], parent); }
            catch (Exception) { /* Parent detects helper termination and retains its pending item. */ }
            Shutdown(); return;
        }
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
        var window = new WorkWindow(new WorkViewModel(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(databasePath)!, "jobs")));
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
