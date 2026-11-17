using System.Windows;
using SpaceInspector.App.ViewModels;

namespace SpaceInspector.App.Views;

public partial class WorkWindow : Window
{
    private bool _closing;
    private bool _closeReady;
    public WorkWindow(WorkViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
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
}
