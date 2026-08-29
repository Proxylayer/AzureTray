using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using AzureTray.Logging;
using AzureTray.Shell;

namespace AzureTray;

public partial class App : System.Windows.Application
{
    private readonly IServiceProvider _services;

    // Tracks the Windows High Contrast setting for the process lifetime;
    // held here so it is rooted for as long as the app runs.
    private HighContrastThemeSwitcher? _highContrast;

    public App(IServiceProvider services)
    {
        _services = services;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before any window exists: if a High Contrast theme is active the
        // system-color palette must already be merged when XAML loads.
        _highContrast = new HighContrastThemeSwitcher(this);

        var startupLog = _services.GetRequiredService<StartupLog>();

        var trayIcon = _services.GetRequiredService<TrayIcon>();
        trayIcon.Start();
        // "The app started but no icon appeared" is a real support case, so the
        // log says explicitly that the icon went visible.
        startupLog.TrayIconVisible();

        var updateService = _services.GetRequiredService<IUpdateService>();
        _ = updateService.CheckOnStartupAsync();

        // The startup check above is fire-and-forget, as is the plugin load, so
        // this marks the UI being up — not every background task being finished.
        startupLog.StartupCompleted();
    }
}
