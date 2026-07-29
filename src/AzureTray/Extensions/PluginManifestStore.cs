using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AzureTray.Extensions;

// What the host recorded about a plugin when it was installed. Written into
// plugins/<packageId>/azuretray-plugin.json by ExtensionInstaller and read
// back by the Settings list and the update checker.
//
// This is the ONLY on-disk record of an installed plugin's version: the
// .nupkg's .nuspec is parsed for the id and then discarded, and the live
// ITrayPlugin instance (whose Version the UI used to show) doesn't exist at
// all when the DLL failed to load. Without the manifest a broken plugin can
// never be told it has a newer version on the feed.
public sealed record InstalledPluginManifest(
    string PackageId,
    string Version,
    DateTimeOffset InstalledUtc)
{
    // Plugin id as reported by the loaded assembly, when known at install
    // time. Null for installs where nothing loaded — PackageId is the key
    // the feed is queried by, so detection never depends on this.
    public string? PluginId { get; init; }

    // Where the bytes came from — the flat-container .nupkg URL for feed
    // installs, null for local-file installs.
    public string? SourceUrl { get; init; }
}

// Reads and writes the per-plugin install manifest. Every operation is
// best-effort: a missing, unreadable, or corrupt manifest degrades to
// "version unknown" (null) and is logged, never thrown — a hand-mangled
// JSON file must not break the Settings window or the update poll.
public sealed class PluginManifestStore
{
    public const string FileName = "azuretray-plugin.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<PluginManifestStore> _logger;

    public PluginManifestStore(IAppPaths paths, ILogger<PluginManifestStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    // Records an install. Overwrites any previous manifest — an in-place
    // version bump must not leave the old version on disk.
    public void Write(InstalledPluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        try
        {
            var dir = Path.Combine(_paths.PluginsDir, manifest.PackageId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(manifest, JsonOptions));
        }
        catch (Exception ex)
        {
            // A plugin that installed fine but couldn't record its manifest
            // still works; it just reports "version unknown" until its next
            // install. Not worth failing the install over.
            _logger.LogWarning(ex,
                "Failed to write install manifest for {PackageId} {Version}; update detection will fall back to the loaded version.",
                manifest.PackageId, manifest.Version);
        }
    }

    // Manifest for plugins/<packageId>/. Null when absent or unreadable.
    public InstalledPluginManifest? TryRead(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return null;
        return TryReadFile(Path.Combine(_paths.PluginsDir, packageId, FileName));
    }

    // Manifest sitting beside an installed DLL. Used by the Settings list,
    // which enumerates DLL paths rather than package ids (and covers the
    // legacy top-level plugins/<id>.dll layout, where there is no folder to
    // hold a manifest — that returns null and degrades to the loaded version).
    public InstalledPluginManifest? TryReadForDll(string installedDllPath)
    {
        if (string.IsNullOrWhiteSpace(installedDllPath)) return null;

        var dir = Path.GetDirectoryName(installedDllPath);
        if (string.IsNullOrEmpty(dir)) return null;

        // A DLL directly in the plugins folder is the legacy layout; its
        // "manifest" would be shared by every legacy plugin, so skip it.
        if (string.Equals(
                Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(_paths.PluginsDir).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TryReadFile(Path.Combine(dir, FileName));
    }

    private InstalledPluginManifest? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var manifest = JsonSerializer.Deserialize<InstalledPluginManifest>(
                File.ReadAllText(path), JsonOptions);

            if (manifest is null
                || string.IsNullOrWhiteSpace(manifest.PackageId)
                || string.IsNullOrWhiteSpace(manifest.Version))
            {
                _logger.LogWarning(
                    "Install manifest at {Path} is missing packageId or version; treating the plugin's version as unknown.",
                    path);
                return null;
            }

            return manifest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read install manifest at {Path}; treating the plugin's version as unknown.", path);
            return null;
        }
    }
}
