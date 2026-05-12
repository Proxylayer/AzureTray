using System.Windows;
using AzureTray.ViewModels;

namespace AzureTray;

public partial class TestRunnerWindow : Window
{
    public TestRunnerWindow(TestRunnerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
