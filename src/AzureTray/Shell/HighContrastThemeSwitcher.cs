using System;
using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;

namespace AzureTray.Shell;

/// <summary>
/// Keeps the application's merged resources in sync with the Windows High
/// Contrast setting: merges the <see cref="HighContrastTheme"/> dictionary
/// while an HC theme is active and removes it when the user leaves HC, live,
/// without a restart.
///
/// Two hooks, both cheap and idempotent through <see cref="Sync"/>:
///  - <see cref="SystemParameters.StaticPropertyChanged"/> fires when the
///    HighContrast flag itself flips (the documented WPF hook), and
///  - <see cref="SystemEvents.UserPreferenceChanged"/> covers switching
///    BETWEEN High Contrast themes (e.g. HC Black → HC White), where the flag
///    stays true but every system color changes, so the dictionary must be
///    rebuilt from the new palette. It can fire on a non-UI thread, hence the
///    dispatcher hop.
/// </summary>
internal sealed class HighContrastThemeSwitcher
{
    private readonly System.Windows.Application _app;
    private ResourceDictionary? _applied;

    /// <summary>
    /// Applies the correct state for the current system setting immediately
    /// and starts tracking changes. Lives for the process lifetime — the app
    /// has exactly one, so the static event subscriptions are never removed.
    /// </summary>
    public HighContrastThemeSwitcher(System.Windows.Application app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        Sync();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Sync();
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color
            or UserPreferenceCategory.Accessibility
            or UserPreferenceCategory.VisualStyle)
        {
            _app.Dispatcher.BeginInvoke(new Action(Sync));
        }
    }

    /// <summary>
    /// Rebuild-from-scratch sync: always drop the currently merged HC
    /// dictionary, then re-merge a fresh one if HC is (still) active. The
    /// rebuild is what picks up a palette change between two HC themes, and
    /// it makes double-firing hooks harmless.
    /// </summary>
    private void Sync()
    {
        if (_applied is not null)
        {
            _app.Resources.MergedDictionaries.Remove(_applied);
            _applied = null;
        }

        if (!SystemParameters.HighContrast)
        {
            return;
        }

        _applied = HighContrastTheme.CreateDictionary();
        _app.Resources.MergedDictionaries.Add(_applied);
    }
}
