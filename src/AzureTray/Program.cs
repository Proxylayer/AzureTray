using System;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
using Serilog.Events;
using Velopack;

namespace AzureTray;

internal static class Program
{
    private static readonly string UserAgent =
        $"AzureTray/{typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    [STAThread]
    public static int Main(string[] args)
    {
        // Started before anything else so "Startup completed in N ms" measures
        // the whole launch, not just the part after the logger exists.
        var startupClock = new StartupClock();

        var appPaths = new AppPaths();
        appPaths.EnsureDirectoriesExist();

        // The bootstrap logger writes to the rolling log file (not just Debug)
        // so events that happen BEFORE the host is built — Velopack's
        // update/restart hooks, the single-instance outcome, and any fatal
        // startup exception — leave a trail on disk. Without this, a relaunch
        // that exits early (e.g. losing the single-instance race after an
        // update) produces zero file evidence, which reads as "the app just
        // didn't come back." The host reconfigures Log.Logger moments later.
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                path: appPaths.LogFileTemplate,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                shared: true,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateBootstrapLogger();

        try
        {
            Log.Information(
                "AzureTray {Version} starting up. ProcessPath={ProcessPath}",
                AppVersion.Display, Environment.ProcessPath);

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
                Log.Information(
                    "AzureTray is already running for this user; exiting without starting a second tray. This exe: {ProcessPath}",
                    Environment.ProcessPath);
                return 0;
            }

            using var host = BuildHost(args, appPaths, startupClock);

            // First point at which the real logging pipeline (file sink + the
            // Log Viewer's ring buffer) exists. Everything known before the host
            // was built — version, resolved paths — is replayed here rather than
            // dropped; the bootstrap logger above only reaches the file.
            var startupLog = host.Services.GetRequiredService<StartupLog>();
            LogStartupContext(host.Services, appPaths, startupLog);

            startupLog.HostedServicesStarting();
            host.Start();
            startupLog.HostedServicesStarted();

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

    // The configuration a support case actually needs, logged once the pipeline
    // that can carry it exists. Only settings nothing else reports: the update
    // feed URL and installed-vs-dev come from UpdateService's own startup line,
    // and both polling loops log their own intervals when they start.
    private static void LogStartupContext(IServiceProvider services, IAppPaths appPaths, StartupLog startupLog)
    {
        // UpdateService owns the installed-vs-dev distinction (it asks Velopack);
        // resolving the singleton here only moves its construction a few
        // milliseconds earlier than the hosted services would have.
        var updateService = services.GetRequiredService<IUpdateService>();

        startupLog.HostBuilt(AppVersion.Display, updateService.IsInstalledBuild);
        startupLog.PathsResolved(appPaths);
        startupLog.LoggingConfiguration(services.GetRequiredService<IOptions<LoggingOptions>>().Value);
        startupLog.AzureCloudConfiguration(services.GetRequiredService<IOptions<AzureCloudOptions>>().Value);
        // Auto-update: the persisted user choice, not the config seed — that is
        // the value that decides what happens on the next poll.
        startupLog.PluginConfiguration(
            services.GetRequiredService<IOptions<PluginOptions>>().Value,
            services.GetRequiredService<PluginUpdatePreferenceStore>().AutoUpdateEnabled);
        startupLog.PluginFeedConfiguration(services.GetRequiredService<IOptions<NuGetPluginFeedOptions>>().Value);
    }

    private static IHost BuildHost(string[] args, AppPaths appPaths, StartupClock startupClock)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Configuration.AddJsonFile(
            appPaths.UserConfigFilePath,
            optional: true,
            reloadOnChange: true);

        builder.Services.AddSingleton<IAppPaths>(appPaths);
        builder.Services.AddSingleton(startupClock);

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

        builder.Services
            .AddOptions<TokenFreshnessOptions>()
            .Bind(builder.Configuration.GetSection(TokenFreshnessOptions.SectionName));

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
        builder.Services.AddSingleton<IPluginHttpClientCore, HostPluginHttpClient>();
        builder.Services.AddSingleton<INuGetPluginFeed, NuGetPluginFeed>();
        builder.Services.AddSingleton<IPackageSecurityScanner, GhsaPackageSecurityScanner>();
        builder.Services.AddSingleton<PluginManifestStore>();
        builder.Services.AddSingleton<IExtensionInstaller, ExtensionInstaller>();
        builder.Services.AddSingleton<IFileDialogService, FileDialogService>();
        builder.Services.AddSingleton<INotifier, NotificationService>();
        builder.Services.AddSingleton<IClipboard, HostClipboard>();
        builder.Services.AddSingleton<IStartupManager, RegistryStartupManager>();
        builder.Services.AddSingleton<ITenantReadinessTracker, TenantReadinessTracker>();
        // Runtime token-renewal health: detection (reactive via HostPluginHttpClient
        // + the hosted background monitor), the persistent re-auth popup, and the
        // shared resolve path. One instance serves the interface and the hosted service.
        builder.Services.AddSingleton<TenantAuthHealthService>();
        builder.Services.AddSingleton<ITenantAuthHealth>(sp => sp.GetRequiredService<TenantAuthHealthService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TenantAuthHealthService>());
        builder.Services.AddSingleton<IPluginConfigStore, PluginConfigStore>();
        builder.Services.AddSingleton<PluginLoader>(sp => new PluginLoader(
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IPluginSignatureVerifier>(),
            sp.GetRequiredService<IOptions<PluginOptions>>(),
            sp.GetRequiredService<IPluginHttpClientCore>(),
            sp.GetRequiredService<ICredentialFactory>(),
            sp.GetRequiredService<INotifier>(),
            sp.GetRequiredService<IClipboard>(),
            sp.GetRequiredService<ITenantStore>(),
            sp.GetRequiredService<IAzureCloudConfig>(),
            sp.GetRequiredService<IExtensionInstaller>(),
            sp.GetRequiredService<ITenantReadinessTracker>(),
            sp.GetRequiredService<IPluginConfigStore>(),
            sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<IPluginLoader>(sp => sp.GetRequiredService<PluginLoader>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PluginLoader>());
        // Probe must register AFTER PluginLoader so plugins subscribe to
        // TenantReady before the first ready event fires.
        builder.Services.AddHostedService<TenantReadinessProbe>();

        // Token freshness. A token minted before an admin-consent change
        // keeps serving the old scope set for its full lifetime, and MSAL's
        // cache survives a restart, so the only cures were Fix permissions /
        // Refresh tokens or waiting the hour out. This loop finds those
        // tokens and force-refreshes them; the gate keeps a scope the tenant
        // will never grant from producing a failed refresh every cycle, and
        // is shared with SettingsViewModel so a user-driven fix re-arms it.
        builder.Services.AddSingleton<ConsentedScopesReader>();
        builder.Services.AddSingleton<TokenFreshnessGate>();
        builder.Services.AddHostedService<TokenFreshnessService>();
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

        builder.Services.AddSingleton<StartupLog>();
        builder.Services.AddSingleton<LogRingBuffer>();
        builder.Services.AddSingleton<ILogEventSink>(sp =>
            new RingBufferSink(sp.GetRequiredService<LogRingBuffer>()));

        var fileSizeLimitBytes = (long)Math.Max(1, loggingOptions.FileSizeLimitMegabytes) * 1024L * 1024L;

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((services, lc) => lc
            .MinimumLevel.ControlledBy(levelSwitch)
            // System.Net.Http emits ~5 Information lines per HTTP request and
            // Microsoft.Extensions.Http adds DefaultHttpClientFactory handler
            // cleanup chatter at Debug — both only restate what
            // HostPluginHttpClient already logs in one line, so they stay at
            // Warning regardless of the app's level.
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
            // Polly is held at Error — the visibility half of the "quiet
            // tiers" scheme. Two knobs work together: PollyTelemetrySeverity
            // (wired in ConfigureHttpClients) decides what severity Polly
            // REPORTS — per-attempt timeouts and the final handled
            // execution-attempt line are demoted to Warning because they only
            // restate retry mechanics; this override decides what is VISIBLE.
            // The net effect is that circuit-breaker-opened and
            // total-request-timeout are deliberately the only Polly voices in
            // the log. The per-request record of a finally-failed call is
            // Tier 1 — HostPluginHttpClient's single Warning line, written
            // after the handler's retries have resolved — plus the call
            // site's Error describing the consequence (Tier 2).
            .MinimumLevel.Override("Polly", LogEventLevel.Error)
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
        // Quiet tiers, reporting half (see PollyTelemetrySeverity for the full
        // story; the visibility half is the Serilog "Polly" override in
        // ConfigureLogging). AddStandardResilienceHandler builds its pipelines
        // with the DI-registered IOptions<TelemetryOptions>, so this single
        // Configure applies to every handler below without replacing any
        // pipeline — options delegates compose, and Polly's own delegate
        // (which sets the logger factory) still runs.
        builder.Services.Configure<Polly.Telemetry.TelemetryOptions>(o =>
            o.SeverityProvider = PollyTelemetrySeverity.Map);

        // ARM/Graph PIM write PUTs (role activation/deactivation) routinely take
        // longer than the standard handler's default 10s per-attempt timeout. When
        // an attempt times out after the server has already committed the write, the
        // retry re-sends it and ARM answers 409 — surfacing a spurious failure for a
        // request that actually succeeded. Raising the per-attempt timeout well past
        // observed PIM write latency keeps the slow path from tripping in the first
        // place (the 409 is also now reconciled as success in ArmPimClient, but this
        // stops it happening for the common case).
        builder.Services.AddHttpClient(HttpClientNames.Graph, ConfigureGraphClient)
            .AddStandardResilienceHandler(ConfigurePimResilience);

        builder.Services.AddHttpClient(HttpClientNames.Arm, ConfigureArmClient)
            .AddStandardResilienceHandler(ConfigurePimResilience);

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

    // Per-attempt 30s comfortably exceeds observed ARM/Graph PIM write latency
    // (the confirmed offender timed out at exactly 10.01s). Total 100s bounds the
    // whole retried operation. The handler validates its own invariants:
    // TotalRequestTimeout >= AttemptTimeout (100 >= 30) and
    // CircuitBreaker.SamplingDuration >= 2 * AttemptTimeout — the 30s default
    // would fail against a 30s attempt, so it is raised to 60s.
    private static void ConfigurePimResilience(HttpStandardResilienceOptions options)
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(100);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(60);
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

        // Plugin updates. Plugins are published to nuget.org independently of
        // the host, so Velopack's self-update says nothing about them: the
        // checker compares installed manifests against the feed, the poll loop
        // runs it every App:Plugins:UpdateCheckIntervalHours (0 disables), and
        // the state object carries the result to the Settings banner and the
        // per-plugin Update buttons. PluginAutoUpdater only ever acts when the
        // user opted in, and refuses anything needing a user decision.
        builder.Services.AddSingleton<PluginUpdatePreferenceStore>();
        builder.Services.AddSingleton<PluginUpdateState>();
        builder.Services.AddSingleton<Notifications.PluginUpdateNotifier>();
        builder.Services.AddSingleton<IPluginUpdateChecker, PluginUpdateChecker>();
        builder.Services.AddSingleton<PluginAutoUpdater>();
        builder.Services.AddHostedService<PluginUpdatePollingService>();
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
