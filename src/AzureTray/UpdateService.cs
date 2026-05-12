using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.Configuration;
using Velopack;
using Velopack.Sources;

namespace AzureTray;

public sealed class UpdateService : IUpdateService
{
    private readonly UpdateFeedOptions _options;
    private readonly UpdateManager? _manager;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(IOptions<UpdateFeedOptions> options, ILogger<UpdateService> logger)
    {
        _options = options.Value;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(_options.FeedUrl))
        {
            _manager = new UpdateManager(new SimpleWebSource(_options.FeedUrl));
        }
        else
        {
            _logger.LogInformation("Update feed URL not configured; update checks disabled.");
        }
    }

    public string CurrentVersionDisplay =>
        _manager is { IsInstalled: true, CurrentVersion: { } v } ? v.ToString() : "dev";

    public async Task CheckOnStartupAsync()
    {
        if (_manager is null || !_manager.IsInstalled) return;

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return;

            await _manager.DownloadUpdatesAsync(info);
            _logger.LogInformation("Update {Version} downloaded; will apply on next exit.", info.TargetFullRelease.Version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Velopack startup check failed");
        }
    }

    public async Task<string> CheckAndApplyAsync()
    {
        if (_manager is null) return "Updates are not configured.";
        if (!_manager.IsInstalled) return "Updates only available in installed builds.";

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return "Up to date.";

            await _manager.DownloadUpdatesAsync(info);
            _logger.LogInformation("Applying update {Version} and restarting.", info.TargetFullRelease.Version);
            _manager.ApplyUpdatesAndRestart(info);
            return "Restarting…";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Velopack update failed");
            return $"Update failed: {ex.Message}";
        }
    }
}
