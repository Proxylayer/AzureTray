using System;
using System.Windows;
using System.Windows.Input;

namespace AzureTray.Notifications;

public partial class NotificationWindow : Window
{
    // Enter-as-default arming delay. Toasts keep ShowActivated (a deliberate
    // choice: the user wants interactive prompts to take focus), which means
    // a toast can appear under the user's hands mid-typing — an in-flight
    // Enter keystroke would instantly submit/confirm a prompt the user never
    // saw. Enter is therefore ignored for a short window after the window is
    // first shown; Esc and mouse clicks are never delayed, so accidental
    // *dismissal-free confirmation* is prevented without trapping the user.
    private static readonly TimeSpan EnterArmingDelay = TimeSpan.FromMilliseconds(300);

    private DateTime? _shownAtUtc;

    public NotificationWindow()
    {
        InitializeComponent();
        ContentRendered += OnFirstContentRendered;
    }

    /// <summary>
    /// Whether enough time has passed since the window appeared for Enter to
    /// act as the default button. Pure so the threshold rule is unit-testable.
    /// </summary>
    internal static bool IsEnterArmed(DateTime? shownAtUtc, DateTime nowUtc)
        => shownAtUtc is { } shown && nowUtc - shown >= EnterArmingDelay;

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        _shownAtUtc = DateTime.UtcNow;

        // A prompt that asks for text should be ready to type into.
        if (DataContext is NotificationViewModel { IsTextInput: true })
        {
            InputBox.Focus();
        }
        else if (DataContext is NotificationViewModel { IsChoice: true })
        {
            FocusChoiceList();
        }
        else if (DataContext is NotificationViewModel { IsYesNo: true })
        {
            // Land on the default (Yes) button so Space/Enter answer
            // immediately; Enter still waits for the arming delay.
            YesButton.Focus();
        }
        else if (DataContext is NotificationViewModel { IsAction: true })
        {
            // Single call-to-action prompt: land on its (IsDefault) button.
            ActionButton.Focus();
        }
    }

    /// <summary>
    /// Puts keyboard focus into the choice list so arrow keys work the moment
    /// the prompt appears. Pre-selects the first item when nothing is selected
    /// — standard dialog ListBox behavior; it only enables Submit, which still
    /// requires explicit activation (armed Enter or a click) to fire.
    /// </summary>
    private void FocusChoiceList()
    {
        if (ChoiceList.SelectedIndex < 0 && ChoiceList.Items.Count > 0)
        {
            ChoiceList.SelectedIndex = 0;
        }

        // Focus the selected item's container (not just the ListBox) so the
        // highlight is visible and arrow keys move the selection immediately.
        if (ChoiceList.SelectedIndex >= 0
            && ChoiceList.ItemContainerGenerator.ContainerFromIndex(ChoiceList.SelectedIndex)
                is System.Windows.Controls.ListBoxItem item)
        {
            item.Focus();
        }
        else
        {
            ChoiceList.Focus();
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            // Swallow Enter until armed — see EnterArmingDelay above. Once
            // armed, the IsDefault button (Yes / Submit / the action) takes it.
            if (!IsEnterArmed(_shownAtUtc, DateTime.UtcNow))
            {
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Yes/No and Submit/Cancel layouts carry an IsCancel button (No /
            // Cancel), which WPF wires to Esc on its own. The other layouts
            // (Information, ActionRequest) have no cancel button, so Esc must
            // be wired here to the dismissive action.
            if (DataContext is NotificationViewModel { IsYesNo: false, IsSubmittable: false } vm
                && vm.DismissCommand.CanExecute(null))
            {
                vm.DismissCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
