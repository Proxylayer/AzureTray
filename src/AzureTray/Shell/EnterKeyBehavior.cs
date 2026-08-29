using System.Windows;
using System.Windows.Input;

namespace AzureTray.Shell;

/// <summary>
/// What pressing Enter should do on an input control decorated with
/// <see cref="EnterKeyBehavior"/>.
/// </summary>
public enum EnterAction
{
    /// <summary>Enter is not handled by the behavior (default).</summary>
    None,

    /// <summary>Enter moves keyboard focus to the next control in tab order.</summary>
    MoveFocusNext,
}

/// <summary>
/// Attached behavior giving any input control (TextBox, PasswordBox, …)
/// declarative Enter-key semantics from XAML:
///
///   shell:EnterKeyBehavior.Action="MoveFocusNext"   — Enter tabs onward
///   shell:EnterKeyBehavior.Command="{Binding X}"    — Enter runs the command
///
/// When both are set the command wins if it can execute; otherwise focus
/// moves. The key event is marked handled only when the behavior actually
/// acted, so an unactionable Enter still reaches default handling.
/// </summary>
public static class EnterKeyBehavior
{
    // ─── Decision core (pure, unit-testable) ─────────────────────────────

    internal enum Decision
    {
        NotHandled,
        ExecuteCommand,
        MoveFocusNext,
    }

    /// <summary>
    /// Decides what an Enter press should do given the attached configuration
    /// and the command's current executability. Pure so it can be unit-tested
    /// without any WPF plumbing.
    /// </summary>
    internal static Decision Decide(EnterAction action, bool hasCommand, bool commandCanExecute)
    {
        if (hasCommand && commandCanExecute) return Decision.ExecuteCommand;
        if (action == EnterAction.MoveFocusNext) return Decision.MoveFocusNext;
        return Decision.NotHandled;
    }

    // ─── Attached properties ─────────────────────────────────────────────

    public static readonly DependencyProperty ActionProperty =
        DependencyProperty.RegisterAttached(
            "Action",
            typeof(EnterAction),
            typeof(EnterKeyBehavior),
            new PropertyMetadata(EnterAction.None, OnConfigurationChanged));

    public static EnterAction GetAction(DependencyObject obj) => (EnterAction)obj.GetValue(ActionProperty);
    public static void SetAction(DependencyObject obj, EnterAction value) => obj.SetValue(ActionProperty, value);

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(EnterKeyBehavior),
            new PropertyMetadata(null, OnConfigurationChanged));

    public static ICommand? GetCommand(DependencyObject obj) => (ICommand?)obj.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject obj, ICommand? value) => obj.SetValue(CommandProperty, value);

    // Tracks whether we've already hooked PreviewKeyDown for this element so
    // setting both attached properties doesn't double-subscribe.
    private static readonly DependencyProperty IsHookedProperty =
        DependencyProperty.RegisterAttached(
            "IsHooked",
            typeof(bool),
            typeof(EnterKeyBehavior),
            new PropertyMetadata(false));

    private static void OnConfigurationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)element.GetValue(IsHookedProperty)) return;

        element.PreviewKeyDown += OnPreviewKeyDown;
        element.SetValue(IsHookedProperty, true);
    }

    private static void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        if (sender is not UIElement element) return;

        var command = GetCommand(element);
        var decision = Decide(GetAction(element), command is not null, command?.CanExecute(null) ?? false);

        switch (decision)
        {
            case Decision.ExecuteCommand:
                command!.Execute(null);
                e.Handled = true;
                break;
            case Decision.MoveFocusNext:
                if (element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
                {
                    e.Handled = true;
                }
                break;
        }
    }
}
