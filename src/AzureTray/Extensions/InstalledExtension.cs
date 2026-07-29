namespace AzureTray.Extensions;

// UI-bindable view of a plugin DLL on disk. IsLoaded reflects whether the
// host's PluginLoader picked it up at startup; LoadedDisplayName / LoadedVersion
// are populated from the live ITrayPlugin instance when so.
//
// The trailing three members describe update state and default to null so a
// row can always be built without them: PackageId and InstalledVersion come
// from the install manifest (falling back to LoadedVersion when there is no
// manifest yet), and AvailableUpdateVersion is set when the update checker
// found something newer on the feed.
public sealed record InstalledExtension(
    string FileName,
    string FullPath,
    bool IsPendingUninstall,
    bool IsLoaded,
    string? PluginId,
    string? LoadedDisplayName,
    string? LoadedVersion,
    string? PackageId = null,
    string? InstalledVersion = null,
    string? AvailableUpdateVersion = null)
{
    // Version we believe is installed. The manifest wins because it exists
    // even when the DLL failed to load — that case has no live instance to
    // read a version from.
    public string? EffectiveVersion => InstalledVersion ?? LoadedVersion;

    public string StatusDisplay => IsPendingUninstall
        ? "Pending uninstall."
        : IsLoaded
            ? $"{LoadedDisplayName ?? FileName}  v{EffectiveVersion ?? "?"}"
            : $"Installed v{EffectiveVersion ?? "?"} (not active — see logs).";

    public bool HasUpdate => !string.IsNullOrEmpty(AvailableUpdateVersion) && !IsPendingUninstall;

    public string UpdateButtonText => HasUpdate ? $"Update to v{AvailableUpdateVersion}" : string.Empty;

    // Shown beside the row so the newer version is visible without opening
    // the toast or the browse list.
    public string UpdateHint => HasUpdate
        ? $"Update available: v{AvailableUpdateVersion} on nuget.org."
        : string.Empty;
}
