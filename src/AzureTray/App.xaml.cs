using System;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

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

        var trayIcon = _services.GetRequiredService<TrayIcon>();
        trayIcon.Start();

        var updateService = _services.GetRequiredService<IUpdateService>();
        _ = updateService.CheckOnStartupAsync();
    }
}
