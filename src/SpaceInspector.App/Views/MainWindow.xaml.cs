using System.Windows;
using SpaceInspector.App.ViewModels;

namespace SpaceInspector.App.Views;

public partial class MainWindow : Window
{
    private bool _closingAfterScan;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
        Closing += async (_, e) =>
        {
            if (!viewModel.IsWorking) return;
            e.Cancel = true;
            if (_closingAfterScan) return;
            _closingAfterScan = true;
            await viewModel.CancelAndWaitAsync();
            // Allow RunScanAsync's UI continuation to finish before a second close request.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Close();
        };
    }
}
