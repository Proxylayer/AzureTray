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
            // GithubSource talks to api.github.com to find the latest published
            // release and its attached RELEASES + .nupkg assets. SimpleWebSource
            // was wrong here — it expects a flat-file directory URL, but the
            // /releases/latest/download alias only resolves when an asset name
            // is appended, not as a directory listing.
            _manager = new UpdateManager(
                new GithubSource(
                    repoUrl: _options.FeedUrl,
                    accessToken: null,
                    prerelease: false));
        }
        else
        {
            _logger.LogInformation("Update feed URL not configured; update checks disabled.");
        }
    }

    public string CurrentVersionDisplay =>
        _manager is { IsInstalled: true, CurrentVersion: { } v } ? v.ToString() : "dev";

    public string? PendingUpdateVersion { get; private set; }

    public event Action<string>? UpdateAvailable;

    public async Task CheckOnStartupAsync()
    {
        if (_manager is null || !_manager.IsInstalled) return;

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return;

            var version = info.TargetFullRelease.Version.ToString();

            // Re-detection guard: the periodic poll calls this same method,
            // so once a version has been surfaced we don't re-download it
            // or re-fire the toast on every interval tick. The Settings
            // banner stays lit via PendingUpdateVersion either way.
            if (string.Equals(PendingUpdateVersion, version, StringComparison.Ordinal))
            {
                return;
            }

            await _manager.DownloadUpdatesAsync(info);
            _logger.LogInformation("Update {Version} downloaded; will apply when the user accepts.", version);

            PendingUpdateVersion = version;
            // Fire after assignment so subscribers can read PendingUpdateVersion
            // synchronously inside the handler.
            UpdateAvailable?.Invoke(version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Velopack update check failed");
        }
    }

    public async Task<string> CheckAndApplyAsync()
    {
        if (_manager is null) return "Updates are not configured.";
        if (!_manager.IsInstalled) return "Updates only available in installed builds.";

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                // No new update — clear any stale pending state so the
                // banner / notification stops surfacing.
                PendingUpdateVersion = null;
                return "Up to date.";
            }

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
