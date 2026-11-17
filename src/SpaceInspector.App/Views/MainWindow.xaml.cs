using System.Windows;
using SpaceInspector.App.ViewModels;

namespace SpaceInspector.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
    }
}
