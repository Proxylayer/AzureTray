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

    // The UpdateInfo whose bits CheckOnStartupAsync (or CheckAndApplyAsync)
    // already downloaded. Holding it lets the banner / Install button apply
    // the staged update offline, without re-querying the feed. Volatile: it's
    // written from background polling and read from the UI thread's click.
    private volatile UpdateInfo? _downloadedUpdate;

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
            _logger.LogInformation(
                "Update feed configured: {FeedUrl}; installed={IsInstalled}, currentVersion={Version}.",
                _options.FeedUrl, _manager.IsInstalled, _manager.CurrentVersion);
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
        if (_manager is null || !_manager.IsInstalled)
        {
            _logger.LogDebug("Update check skipped: not running as an installed Velopack app.");
            return;
        }

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                _logger.LogDebug(
                    "Startup update check: no newer release. Feed={FeedUrl}, currentVersion={Version}.",
                    _options.FeedUrl, _manager.CurrentVersion);
                return;
            }

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

            // Remember the staged update so the banner / Install button can
            // apply it directly, even if the feed is unreachable at click time.
            _downloadedUpdate = info;
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
                _downloadedUpdate = null;
                PendingUpdateVersion = null;
                _logger.LogInformation(
                    "Update check returned no newer release. Feed={FeedUrl}, currentVersion={Version}.",
                    _options.FeedUrl, _manager.CurrentVersion);
                return "Up to date.";
            }

            _logger.LogInformation(
                "Update available: {Target} (current {Current}). Downloading.",
                info.TargetFullRelease.Version, _manager.CurrentVersion);
            await _manager.DownloadUpdatesAsync(info);
            _downloadedUpdate = info;
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

    public Task<string> ApplyPendingUpdateAndRestartAsync()
    {
        if (_manager is null) return Task.FromResult("Updates are not configured.");
        if (!_manager.IsInstalled) return Task.FromResult("Updates only available in installed builds.");

        var staged = _downloadedUpdate;
        if (staged is null)
        {
            // Nothing pre-downloaded (e.g. the banner was seeded from a prior
            // process, or the download hasn't finished). Do the full path —
            // it checks, downloads, and applies.
            _logger.LogInformation("No pre-downloaded update staged; falling back to check+download+apply.");
            return CheckAndApplyAsync();
        }

        try
        {
            _logger.LogInformation(
                "Applying pre-downloaded update {Version} and restarting (no re-check).",
                staged.TargetFullRelease.Version);
            // Exits and relaunches the process; the return below is rarely seen.
            _manager.ApplyUpdatesAndRestart(staged);
            return Task.FromResult("Restarting…");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the pre-downloaded update failed");
            return Task.FromResult($"Update failed: {ex.Message}");
        }
    }
}
