using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using AzureTray.Logging;

namespace AzureTray;

public partial class App : System.Windows.Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
