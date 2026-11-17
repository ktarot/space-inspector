using System.Windows;
using SpaceInspector.App.ViewModels;
using SpaceInspector.App.Views;
using SpaceInspector.Infrastructure.Database;
using SpaceInspector.Infrastructure.FileSystem;

namespace SpaceInspector.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var databasePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpaceInspector", "index.db");
        var viewModel = new MainViewModel(new LocalDriveService(), new SqliteIndexDatabase(databasePath));
        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }
}
