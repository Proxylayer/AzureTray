using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

        // The window/VM is cached and reused by TrayIcon, so detach the VM's
        // singleton event subscriptions when the window closes.
        Closed += (_, _) => viewModel.Cleanup();

        // handledEventsToo: several inner controls (single-line TextBoxes'
        // internal ScrollViewer, ComboBoxes, the plugin ListBoxes once they
        // hit their scroll end) mark MouseWheel handled even when they can't
        // consume it, which strands the wheel over those controls. The hook
        // forwards those already-handled events to the outer scroller — see
        // OnSettingsScrollMouseWheel for the rule.
        SettingsScroll.AddHandler(
            MouseWheelEvent,
            new MouseWheelEventHandler(OnSettingsScrollMouseWheel),
            handledEventsToo: true);
    }

    // Wheel rule: if an inner ScrollViewer handled the event AND can still
    // scroll in the wheel's direction, it keeps it (its own scroll already
    // happened). Otherwise the outer settings scroller takes over. Unhandled
    // events (wheel over empty space / plain buttons) are left alone — the
    // outer ScrollViewer scrolls those natively.
    private void OnSettingsScrollMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!e.Handled || e.Delta == 0) return;

        var inner = FindAncestorScrollViewer(e.OriginalSource as DependencyObject);
        if (inner is not null && CanScrollVertically(inner, e.Delta)) return;

        SettingsScroll.ScrollToVerticalOffset(SettingsScroll.VerticalOffset - e.Delta);
    }

    // Innermost ScrollViewer above the event source, stopping at (and
    // excluding) the outer settings scroller itself.
    private ScrollViewer? FindAncestorScrollViewer(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, SettingsScroll))
        {
            if (source is ScrollViewer viewer) return viewer;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    private static bool CanScrollVertically(ScrollViewer viewer, int delta) =>
        delta < 0
            ? viewer.VerticalOffset < viewer.ScrollableHeight - 0.5
            : viewer.VerticalOffset > 0.5;

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    // Layered Esc: while the tenant edit form is active the first Esc backs
    // out of the edit (CancelEditCommand) and is swallowed; only a further
    // Esc reaches the Close button's IsCancel handling and closes the window.
    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape) return;
        if (DataContext is not SettingsViewModel vm) return;

        if (vm.IsEditingTenant && vm.CancelEditCommand.CanExecute(null))
        {
            vm.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }

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
