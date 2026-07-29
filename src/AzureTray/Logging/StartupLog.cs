using System;
using Microsoft.Extensions.Logging;
using AzureTray.Configuration;
using AzureTray.Extensions;

namespace AzureTray.Logging;

// The Information-level narrative of host startup: which version came up, from
// where, with which configuration in effect, and how long it took. One readable
// line per transition — per-file / per-registration detail belongs at Debug in
// whichever class owns it, and the classes that do the work (tenant store,
// plugin loader, update service, polling loops) still report their own outcomes.
//
// Deliberately fed from the composition root rather than injecting every options
// type here: Program already holds this configuration, and keeping this class to
// a logger plus a clock leaves it with exactly one job — writing the narrative.
//
// These events land in the rolling file sink AND the in-app Log Viewer's
// 500-entry ring buffer, so they are phrased to read well in both.
internal sealed class StartupLog
{
    private readonly ILogger<StartupLog> _logger;
    private readonly StartupClock _clock;

    public StartupLog(ILogger<StartupLog> logger, StartupClock clock)
    {
        _logger = logger;
        _clock = clock;
    }

    public void HostBuilt(string version, bool installedBuild) =>
        _logger.LogInformation(
            "AzureTray {Version} starting as {BuildKind} (process {ProcessId}).",
            version,
            installedBuild ? "an installed build" : "a dev run",
            Environment.ProcessId);

    public void PathsResolved(IAppPaths paths) =>
        _logger.LogInformation(
            "Paths in use: data {DataDir}, plugins {PluginsDir}, logs {LogsDir}, config {ConfigDir}.",
            paths.DataDir, paths.PluginsDir, paths.LogsDir, paths.ConfigDir);

    // The framework-category floor is worth a line: a support log that is missing
    // the HttpClient / Polly per-request detail should say why it is missing.
    public void LoggingConfiguration(LoggingOptions options) =>
        _logger.LogInformation(
            "Logging at minimum level {MinimumLevel}; file logging {FileLogging}, keeping {RetainedFileCount} files of up to {FileSizeLimitMegabytes} MB. Framework HTTP/resilience categories (System.Net.Http, Polly) are held at Warning.",
            options.MinimumLevel,
            options.LogToDisk ? "enabled" : "disabled",
            options.RetainedFileCount,
            options.FileSizeLimitMegabytes);

    public void AzureCloudConfiguration(AzureCloudOptions options) =>
        _logger.LogInformation(
            "Azure cloud authority {Authority}; Graph {GraphEndpoint}, ARM {ArmEndpoint}.",
            options.Authority, options.GraphEndpoint, options.ArmEndpoint);

    public void PluginConfiguration(PluginOptions options, bool autoUpdateEnabled) =>
        _logger.LogInformation(
            "Plugin trust mode {TrustMode}; automatic plugin updates {AutoUpdate}.",
            options.TrustMode, autoUpdateEnabled ? "on" : "off");

    public void PluginFeedConfiguration(NuGetPluginFeedOptions options) =>
        _logger.LogInformation(
            "Plugin feed {SearchUrl} filtered by tag {DiscoveryTag}; prereleases {Prereleases} by default.",
            options.SearchUrl,
            options.DiscoveryTag,
            options.IncludePrereleaseByDefault ? "included" : "excluded");

    public void HostedServicesStarting() =>
        _logger.LogInformation("Host built; starting hosted services.");

    public void HostedServicesStarted() =>
        _logger.LogInformation("Hosted services started {ElapsedMs} ms into startup.", ElapsedMs);

    public void TrayIconVisible() =>
        _logger.LogInformation("Tray icon is visible; the app is ready to use.");

    public void StartupCompleted() =>
        _logger.LogInformation("Startup completed in {ElapsedMs} ms.", ElapsedMs);

    private long ElapsedMs => (long)_clock.Elapsed.TotalMilliseconds;
}
