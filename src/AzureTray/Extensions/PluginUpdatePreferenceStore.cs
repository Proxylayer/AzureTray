using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.Configuration;

namespace AzureTray.Extensions;

// The two user-owned choices around plugin updates, persisted next to the
// other user state (%APPDATA%\AzureTray\plugin-updates.json) rather than in
// appsettings.json, which is host-shipped configuration the user's profile
// must not have to rewrite.
//
// Defaults come from config on first run: auto-update is always off until
// the user opts in, and the prerelease preference seeds from
// App:NuGet:IncludePrereleaseByDefault. Both the Settings browse list and the
// update checker read the same value from here, which is what keeps their
// feed queries on the same cache key (see NuGetPluginFeed's single cache slot).
public sealed class PluginUpdatePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string FileName = "plugin-updates.json";

    private readonly IAppPaths _paths;
    private readonly ILogger<PluginUpdatePreferenceStore> _logger;
    private readonly object _writeLock = new();

    private bool _autoUpdateEnabled;
    private bool _includePrerelease;

    public PluginUpdatePreferenceStore(
        IAppPaths paths,
        IOptions<NuGetPluginFeedOptions> feedOptions,
        IOptions<PluginOptions> pluginOptions,
        ILogger<PluginUpdatePreferenceStore> logger)
    {
        ArgumentNullException.ThrowIfNull(feedOptions);
        ArgumentNullException.ThrowIfNull(pluginOptions);

        _paths = paths;
        _logger = logger;

        // Config-derived defaults first, then whatever the user chose last.
        _autoUpdateEnabled = pluginOptions.Value.AutoUpdate;
        _includePrerelease = feedOptions.Value.IncludePrereleaseByDefault;

        var persisted = TryLoad();
        if (persisted is not null)
        {
            _autoUpdateEnabled = persisted.AutoUpdate ?? _autoUpdateEnabled;
            _includePrerelease = persisted.IncludePrerelease ?? _includePrerelease;
        }
    }

    // Opt-in unattended plugin updates. Default off, and the auto-updater
    // still refuses anything that would need a user decision.
    public bool AutoUpdateEnabled
    {
        get => _autoUpdateEnabled;
        set
        {
            if (_autoUpdateEnabled == value) return;
            _autoUpdateEnabled = value;
            Persist();
        }
    }

    // Whether prerelease versions count as installable / as updates.
    public bool IncludePrerelease
    {
        get => _includePrerelease;
        set
        {
            if (_includePrerelease == value) return;
            _includePrerelease = value;
            Persist();
        }
    }

    private PersistedPreferences? TryLoad()
    {
        var path = Path.Combine(_paths.ConfigDir, FileName);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PersistedPreferences>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            // Corrupt file: fall back to the config defaults. The next write
            // replaces it with something valid.
            _logger.LogWarning(ex,
                "Could not read plugin update preferences at {Path}; using defaults.", path);
            return null;
        }
    }

    private void Persist()
    {
        var path = Path.Combine(_paths.ConfigDir, FileName);
        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(_paths.ConfigDir);
                File.WriteAllText(
                    path,
                    JsonSerializer.Serialize(
                        new PersistedPreferences
                        {
                            AutoUpdate = _autoUpdateEnabled,
                            IncludePrerelease = _includePrerelease,
                        },
                        JsonOptions));
            }
        }
        catch (Exception ex)
        {
            // In-memory value still applies for this session.
            _logger.LogWarning(ex, "Failed to persist plugin update preferences to {Path}.", path);
        }
    }

    // Nullable members so an older/partial file only overrides what it
    // actually carries.
    private sealed class PersistedPreferences
    {
        public bool? AutoUpdate { get; init; }
        public bool? IncludePrerelease { get; init; }
    }
}
