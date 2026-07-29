using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugins;

namespace AzureTray.Extensions;

// Detection half of plugin updates: read what's installed, ask the feed what
// exists, compare with real SemVer precedence.
//
// Feed cache: this calls FetchAsync with exactly the arguments the Settings
// browse list uses — query null, prerelease from PluginUpdatePreferenceStore
// (the same value the checkbox is bound to). NuGetPluginFeed keeps a single
// cache slot keyed by (query, includePrerelease), so identical arguments mean
// the poll can never evict the list the UI is showing; it refreshes it.
internal sealed class PluginUpdateChecker : IPluginUpdateChecker
{
    private readonly IExtensionInstaller _installer;
    private readonly PluginManifestStore _manifests;
    private readonly INuGetPluginFeed _feed;
    private readonly IPluginLoader _loader;
    private readonly PluginUpdatePreferenceStore _preferences;
    private readonly ILogger<PluginUpdateChecker> _logger;

    public PluginUpdateChecker(
        IExtensionInstaller installer,
        PluginManifestStore manifests,
        INuGetPluginFeed feed,
        IPluginLoader loader,
        PluginUpdatePreferenceStore preferences,
        ILogger<PluginUpdateChecker> logger)
    {
        _installer = installer;
        _manifests = manifests;
        _feed = feed;
        _loader = loader;
        _preferences = preferences;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PluginUpdate>> CheckAsync(CancellationToken cancellationToken)
    {
        var installed = BuildInstalledIndex();
        if (installed.Count == 0)
        {
            return Array.Empty<PluginUpdate>();
        }

        var includePrerelease = _preferences.IncludePrerelease;
        var entries = await _feed.FetchAsync(
            query: null,
            includePrerelease: includePrerelease,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var updates = new List<PluginUpdate>();
        foreach (var entry in entries)
        {
            var packageId = entry.NuGetPackageId ?? entry.Id;
            if (!installed.TryGetValue(packageId, out var current)) continue;

            if (!PluginVersions.TryParse(current.Version, out var installedVersion))
            {
                // Either no manifest and nothing loaded, or a version string
                // that isn't SemVer. Never guess an update from that.
                _logger.LogDebug(
                    "Skipping update check for {PackageId}: installed version '{Version}' is unknown or unparseable.",
                    packageId, current.Version ?? "(none)");
                continue;
            }

            var latest = PluginVersions.SelectLatest(entry, includePrerelease);
            if (latest is null || !PluginVersions.TryParse(latest.Version, out var latestVersion))
            {
                _logger.LogDebug(
                    "Skipping update check for {PackageId}: the feed offers no parseable version.", packageId);
                continue;
            }

            if (!PluginVersions.IsNewer(latestVersion, installedVersion, includePrerelease)) continue;

            updates.Add(new PluginUpdate(
                PackageId: packageId,
                PluginId: current.PluginId,
                InstalledVersion: current.Version!,
                InstalledDllPath: current.DllPath,
                Entry: entry,
                Latest: latest));
        }

        if (updates.Count > 0)
        {
            _logger.LogInformation(
                "Plugin update check: {Count} update(s) available — {Summary}.",
                updates.Count,
                string.Join("; ", updates.Select(u => u.SummaryLine)));
        }

        return updates;
    }

    // packageId → what's on disk. The manifest is authoritative for both the
    // id and the version; when it's missing (pre-manifest install, legacy
    // top-level DLL, corrupt file) we fall back to the folder/file name for
    // the id and the live plugin instance for the version, so existing
    // installs keep working until their next update writes a manifest.
    private Dictionary<string, InstalledPlugin> BuildInstalledIndex()
    {
        var loadedByPath = new Dictionary<string, LoadedPlugin>(StringComparer.OrdinalIgnoreCase);
        foreach (var loaded in _loader.LoadedPlugins)
        {
            loadedByPath[Path.GetFullPath(loaded.AssemblyPath)] = loaded;
        }

        var pending = _installer.ListPendingUninstalls().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = new Dictionary<string, InstalledPlugin>(StringComparer.OrdinalIgnoreCase);

        foreach (var dllPath in _installer.ListInstalledDlls())
        {
            if (pending.Contains(Path.GetFileName(dllPath))) continue;

            var manifest = _manifests.TryReadForDll(dllPath);
            loadedByPath.TryGetValue(Path.GetFullPath(dllPath), out var loaded);

            var packageId = manifest?.PackageId ?? InferPackageId(dllPath);
            if (string.IsNullOrEmpty(packageId)) continue;

            index[packageId] = new InstalledPlugin(
                Version: manifest?.Version ?? loaded?.Plugin.Version,
                DllPath: dllPath,
                PluginId: loaded?.Plugin.Id ?? manifest?.PluginId);
        }

        return index;
    }

    // plugins/<packageId>/<packageId>.dll → the folder name; a legacy
    // top-level plugins/<name>.dll → the file name.
    private static string InferPackageId(string dllPath)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(dllPath) ?? string.Empty);
        return string.IsNullOrEmpty(folder) || string.Equals(folder, "plugins", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(dllPath)
            : folder;
    }

    private sealed record InstalledPlugin(string? Version, string DllPath, string? PluginId);
}
