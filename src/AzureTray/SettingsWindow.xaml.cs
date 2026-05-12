using System.Windows;
using System.Windows.Controls;
using AzureTray.AppRegistration;
using AzureTray.Shell;
using AzureTray.ViewModels;

namespace AzureTray;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        this.EnableDarkTitleBar();
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    // Split button's chevron half: drop the ContextMenu below the button.
    // WPF doesn't open Button.ContextMenu on left-click by default
    // (that's the right-click contract); for a split button we want it
    // opened by a normal click on the chevron.
    private void OnModeDropdownClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }
    }

    private void AppRegistrationResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is AppRegistrationInfo info)
        {
            vm.SelectAppRegistration(info);
        }
    }
}
