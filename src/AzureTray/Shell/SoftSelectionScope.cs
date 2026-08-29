using System;
using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

// Disambiguate from the System.Drawing types (in scope because
// UseWindowsForms is enabled at the project level for the tray NotifyIcon).
using Brush = System.Windows.Media.Brush;
using SystemColors = System.Windows.SystemColors;

namespace AzureTray.Shell;

/// <summary>
/// Attached behavior that softens the system selection highlight inside a
/// scope (a Window or any container): the <see cref="SystemColors"/> selection
/// brush keys are overridden in the scope's Resources with the theme's shared
/// <c>Brush.Selection.*</c> set, so accent-blue selection doesn't drown out
/// row text while High Contrast still gets the real Highlight/HighlightText
/// pair (the HC dictionary remaps <c>Brush.Selection.*</c> to system colors).
///
/// This exists because the XAML spelling of the same idea —
/// <c>&lt;StaticResource x:Key="{x:Static SystemColors.HighlightBrushKey}"
/// ResourceKey="Brush.Selection.Soft"/&gt;</c> — crashes at window load:
/// Baml2006Reader throws a NullReferenceException in Process_KeyElementEnd
/// while pre-reading the keys of a compiled (deferred) ResourceDictionary
/// that contains a keyed StaticResource entry. Aliasing in code sidesteps
/// the reader entirely.
///
/// The mapping also re-resolves live when the system theme changes (same
/// hooks as <see cref="HighContrastThemeSwitcher"/>, dispatcher-queued so
/// the switcher's dictionary swap lands first), so a High Contrast toggle
/// no longer requires reopening the window.
/// </summary>
internal static class SoftSelectionScope
{
    private static readonly (ResourceKey SystemKey, string ThemeKey)[] Map =
    {
        (SystemColors.HighlightBrushKey, "Brush.Selection.Soft"),
        (SystemColors.HighlightTextBrushKey, "Brush.Selection.SoftText"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "Brush.Selection.SoftInactive"),
        (SystemColors.InactiveSelectionHighlightTextBrushKey, "Brush.Selection.SoftText"),
        (SystemColors.ControlBrushKey, "Brush.Selection.SoftInactive"),
    };

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SoftSelectionScope),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(FrameworkElement element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(FrameworkElement element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is true)
        {
            // The tracker wires itself to the element's lifetime; no one
            // needs to hold a reference to it.
            _ = new Tracker(element);
        }
    }

    /// <summary>
    /// Per-scope lifetime: applies the overrides once the element is loaded
    /// (theme lookups need the element in the tree), re-applies on system
    /// theme changes, and unhooks the static events when the hosting window
    /// closes so the element can be collected.
    /// </summary>
    private sealed class Tracker
    {
        private readonly FrameworkElement _element;

        public Tracker(FrameworkElement element)
        {
            _element = element;
            if (element.IsLoaded)
            {
                Attach();
            }
            else
            {
                element.Loaded += OnLoaded;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _element.Loaded -= OnLoaded;
            Attach();
        }

        private void Attach()
        {
            Apply();

            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            var window = _element as Window ?? Window.GetWindow(_element);
            if (window is not null)
            {
                window.Closed += OnScopeGone;
            }
            else
            {
                _element.Unloaded += OnScopeGone;
            }
        }

        private void OnScopeGone(object? sender, EventArgs e)
        {
            SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        private void Apply()
        {
            foreach (var (systemKey, themeKey) in Map)
            {
                if (_element.TryFindResource(themeKey) is Brush brush)
                {
                    _element.Resources[systemKey] = brush;
                }
            }
        }

        // Same triggers HighContrastThemeSwitcher listens for. Always hop via
        // the dispatcher queue so Apply runs AFTER the switcher has swapped
        // the app-level HC dictionary (the switcher subscribed first, so its
        // handler — and the dispatcher work it queues — runs before ours).
        private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SystemParameters.HighContrast))
            {
                _element.Dispatcher.BeginInvoke(new Action(Apply));
            }
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category is UserPreferenceCategory.Color
                or UserPreferenceCategory.Accessibility
                or UserPreferenceCategory.VisualStyle)
            {
                _element.Dispatcher.BeginInvoke(new Action(Apply));
            }
        }
    }
}
