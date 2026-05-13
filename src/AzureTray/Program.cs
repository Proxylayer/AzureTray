using System;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using AzureTray.AppRegistration;
using AzureTray.Auth;
using AzureTray.AzureCloud;
using AzureTray.Extensions;
using AzureTray.Shell;
using AzureTray.Configuration;
using AzureTray.Graph;
using AzureTray.Logging;
using AzureTray.Notifications;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using AzureTray.Tenants;
using AzureTray.Testing;
using AzureTray.ViewModels;
using Serilog;
using Serilog.Core;
using Velopack;

namespace AzureTray;

internal static class Program
{
    private static readonly string UserAgent =
        $"AzureTray/{typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    [STAThread]
    public static int Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

        try
        {
            VelopackApp.Build()
                .OnFirstRun(v => Log.Information("Velopack: first run after install of v{Version}", v))
                .OnRestarted(v => Log.Information("Velopack: restarted into v{Version}", v))
                .Run();

            // Velopack already exits early when this invocation is a
            // setup / update / uninstall step. Past this point we're a
            // normal app launch — refuse to start if another instance
            // is already running for this user.
            using var singleInstance = new SingleInstanceLock();
            if (!singleInstance.Acquired)
            {
                Log.Information("AzureTray is already running for this user; exiting without starting a second tray.");
                return 0;
            }

            using var host = BuildHost(args);
            host.Start();

            var app = host.Services.GetRequiredService<App>();
            app.InitializeComponent();
            var exitCode = app.Run();

            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return exitCode;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Host terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static IHost BuildHost(string[] args)
    {
        var appPaths = new AppPaths();
        appPaths.EnsureDirectoriesExist();

        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration.AddJsonFile(
            appPaths.UserConfigFilePath,
            optional: true,
            reloadOnChange: true);

        builder.Services.AddSingleton<IAppPaths>(appPaths);

        ConfigureOptions(builder);
        ConfigureLogging(builder, appPaths);
        ConfigureHttpClients(builder);
        ConfigureApplication(builder);

        return builder.Build();
    }

    private static void ConfigureOptions(HostApplicationBuilder builder)
    {
        builder.Services
            .AddOptions<UpdateFeedOptions>()
            .Bind(builder.Configuration.GetSection(UpdateFeedOptions.SectionName));

        builder.Services
            .AddOptions<LoggingOptions>()
            .Bind(builder.Configuration.GetSection(LoggingOptions.SectionName));

        builder.Services
            .AddOptions<AzureCloudOptions>()
            .Bind(builder.Configuration.GetSection(AzureCloudOptions.SectionName));

        builder.Services
            .AddOptions<AuthOptions>()
            .Bind(builder.Configuration.GetSection(AuthOptions.SectionName));

        builder.Services
            .AddOptions<PluginOptions>()
            .Bind(builder.Configuration.GetSection(PluginOptions.SectionName));

        builder.Services
            .AddOptions<NuGetPluginFeedOptions>()
            .Bind(builder.Configuration.GetSection(NuGetPluginFeedOptions.SectionName));

        builder.Services.AddSingleton<IAzureCloudConfig, AzureCloudConfig>();
        builder.Services.AddSingleton<ITenantStore, JsonFileTenantStore>();
        builder.Services.AddSingleton<ICredentialFactory, CredentialFactory>();
        builder.Services.AddSingleton<AppRegistration.Internal.AppRegistrationGraphClient>();
        builder.Services.AddSingleton<IAppRegistrationDiscovery, AppRegistration.AppRegistrationDiscovery>();
        builder.Services.AddSingleton<IAppRegistrationPermissions, AppRegistration.AppRegistrationPermissions>();
        builder.Services.AddSingleton<IAppRegistrationProvisioning, AppRegistration.AppRegistrationProvisioning>();
        builder.Services.AddSingleton<IOpenIdConfigClient, OpenIdConfigClient>();
        builder.Services.AddSingleton<IWindowsAccountSignInService, WindowsAccountSignInService>();

        builder.Services.AddSingleton<IPluginSignatureVerifier, AuthenticodePluginSignatureVerifier>();
        builder.Services.AddSingleton<IPluginHttpClient, HostPluginHttpClient>();
        builder.Services.AddSingleton<INuGetPluginFeed, NuGetPluginFeed>();
        builder.Services.AddSingleton<IPackageSecurityScanner, GhsaPackageSecurityScanner>();
        builder.Services.AddSingleton<IExtensionInstaller, ExtensionInstaller>();
        builder.Services.AddSingleton<IFileDialogService, FileDialogService>();
        builder.Services.AddSingleton<INotifier, NotificationService>();
        builder.Services.AddSingleton<IClipboard, HostClipboard>();
        builder.Services.AddSingleton<ITenantReadinessTracker, TenantReadinessTracker>();
        builder.Services.AddSingleton<IPluginConfigStore, PluginConfigStore>();
        builder.Services.AddSingleton<PluginLoader>();
        builder.Services.AddSingleton<IPluginLoader>(sp => sp.GetRequiredService<PluginLoader>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PluginLoader>());
        // Probe must register AFTER PluginLoader so plugins subscribe to
        // TenantReady before the first ready event fires.
        builder.Services.AddHostedService<TenantReadinessProbe>();
    }

    private static void ConfigureLogging(HostApplicationBuilder builder, AppPaths appPaths)
    {
        var loggingOptions = builder.Configuration
            .GetSection(LoggingOptions.SectionName)
            .Get<LoggingOptions>() ?? new LoggingOptions();

        var levelSwitch = new LoggingLevelSwitch(loggingOptions.MinimumLevel);
        builder.Services.AddSingleton(levelSwitch);

        var fileLoggingSwitch = new FileLoggingSwitch(loggingOptions.LogToDisk);
        builder.Services.AddSingleton(fileLoggingSwitch);

        builder.Services.AddSingleton<LogRingBuffer>();
        builder.Services.AddSingleton<ILogEventSink>(sp =>
            new RingBufferSink(sp.GetRequiredService<LogRingBuffer>()));

        var fileSizeLimitBytes = (long)Math.Max(1, loggingOptions.FileSizeLimitMegabytes) * 1024L * 1024L;

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((services, lc) => lc
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.FromLogContext()
            .ReadFrom.Services(services)
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            // Disk sink is gated by FileLoggingSwitch.Enabled — flipping it at
            // runtime starts / stops file emission without rebuilding the pipeline.
            // Rolling: a new file per day AND when the current file exceeds the
            // size limit. Retains a fixed number of files total.
            .WriteTo.Conditional(
                _ => fileLoggingSwitch.Enabled,
                sub => sub.File(
                    path: appPaths.LogFileTemplate,
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    fileSizeLimitBytes: fileSizeLimitBytes,
                    retainedFileCountLimit: loggingOptions.RetainedFileCount,
                    shared: true,
                    formatProvider: CultureInfo.InvariantCulture,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")));
    }

    private static void ConfigureHttpClients(HostApplicationBuilder builder)
    {
        builder.Services.AddHttpClient(HttpClientNames.Graph, ConfigureGraphClient)
            .AddStandardResilienceHandler();

        builder.Services.AddHttpClient(HttpClientNames.Arm, ConfigureArmClient)
            .AddStandardResilienceHandler();

        // NuGet search client — queries nuget.org's v3 search API for
        // packages carrying the host's discovery tag.
        builder.Services.AddHttpClient(NuGetPluginFeed.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }).AddStandardResilienceHandler();

        // Plugin download client — fetches the .nupkg from nuget.org's
        // flat-container endpoint.
        builder.Services.AddHttpClient(ExtensionInstaller.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        }).AddStandardResilienceHandler();

        // GHSA API requires a User-Agent and an Accept header that
        // requests the v3 advisory schema. Anonymous access is fine for
        // public advisories; rate limit is 60/hour per IP.
        builder.Services.AddHttpClient(GhsaPackageSecurityScanner.HttpClientName, client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }).AddStandardResilienceHandler();
    }

    private static void ConfigureGraphClient(IServiceProvider sp, System.Net.Http.HttpClient client)
    {
        var cloud = sp.GetRequiredService<IAzureCloudConfig>();
        client.BaseAddress = cloud.GraphEndpoint;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    private static void ConfigureArmClient(IServiceProvider sp, System.Net.Http.HttpClient client)
    {
        var cloud = sp.GetRequiredService<IAzureCloudConfig>();
        client.BaseAddress = cloud.ArmEndpoint;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    private static void ConfigureApplication(HostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<App>();
        builder.Services.AddSingleton<TrayIcon>();
        builder.Services.AddSingleton<IUpdateService, UpdateService>();
        // Surfaces an ActionRequest notification with a blue "Update now"
        // button as soon as UpdateService detects + downloads a release.
        builder.Services.AddHostedService<Notifications.UpdateAvailableNotifier>();
        // Background loop that re-runs the startup check every
        // UpdateFeedOptions.CheckIntervalHours so a long-running tray
        // session still catches new releases without a restart.
        builder.Services.AddHostedService<UpdatePollingService>();
        builder.Services.AddSingleton<IGraphMeClient, GraphMeClient>();
        builder.Services.AddSingleton<IGraphOrganizationClient, GraphOrganizationClient>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<SettingsWindow>();
        builder.Services.AddTransient<LogViewerViewModel>();
        builder.Services.AddTransient<LogViewerWindow>();
        builder.Services.AddSingleton<ITestRegistry, TestRegistry>();
        builder.Services.AddTransient<TestRunnerViewModel>();
        builder.Services.AddTransient<TestRunnerWindow>();
    }
}
