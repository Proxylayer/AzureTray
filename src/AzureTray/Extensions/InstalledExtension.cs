namespace AzureTray.Extensions;

// UI-bindable view of a plugin DLL on disk. IsLoaded reflects whether the
// host's PluginLoader picked it up at startup; LoadedDisplayName / LoadedVersion
// are populated from the live ITrayPlugin instance when so.
public sealed record InstalledExtension(
    string FileName,
    string FullPath,
    bool IsPendingUninstall,
    bool IsLoaded,
    string? PluginId,
    string? LoadedDisplayName,
    string? LoadedVersion)
{
    public string StatusDisplay => IsPendingUninstall
        ? "Pending uninstall."
        : IsLoaded
            ? $"{LoadedDisplayName ?? FileName}  v{LoadedVersion ?? "?"}"
            : "Installed (not active — see logs).";
}
