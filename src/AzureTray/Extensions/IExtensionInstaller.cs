using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Extensions;

public interface IExtensionInstaller
{
    // Installs a .nupkg from a local path. Extracts every DLL from
    // lib/<tfm>/ (preferring net8.0) into plugins/<id>/ where <id> is
    // taken from the package's .nuspec. Returns the full paths actually
    // written.
    Task<IReadOnlyList<string>> InstallFromFileAsync(string sourcePath, CancellationToken cancellationToken);

    // Downloads the .nupkg at `downloadUrl` and installs every DLL from
    // its lib/<tfm>/ folder (preferring net8.0) into a per-plugin
    // subfolder named for `packageId`.
    //
    // ChecksumSha256, when non-null, is verified against the downloaded
    // bytes before extraction — install aborts if the hash mismatches.
    // Returns the list of target paths actually written.
    Task<IReadOnlyList<string>> InstallFromUrlAsync(
        string packageId,
        string downloadUrl,
        string? checksumSha256,
        CancellationToken cancellationToken);

    // Hot-deletes an installed plugin's files. For top-level legacy DLLs
    // (plugins/X.dll) this removes the single file. For per-plugin
    // subfolders (plugins/X/X.dll) it removes the entire subfolder so
    // bundled transitive deps go away too. Includes a short retry loop
    // because the OS sometimes holds the file lock briefly after the
    // collectible AssemblyLoadContext unloads. Returns true if every
    // target was removed.
    Task<bool> TryDeleteAsync(string installedDllPath, CancellationToken cancellationToken);

    // Marks the given installed DLL for deletion on next host startup.
    // Use only as the fallback when TryDeleteAsync reports the files
    // are still locked.
    Task RequestUninstallAsync(string installedDllPath, CancellationToken cancellationToken);

    // All plugin DLLs currently in the plugins folder — top-level legacy
    // *.dll plus the primary <id>.dll of each subfolder plugin.
    IReadOnlyList<string> ListInstalledDlls();

    // File names (not full paths) of DLLs that have a pending-uninstall sentinel.
    IReadOnlyList<string> ListPendingUninstalls();

    // Opens the plugins folder in Windows Explorer.
    void OpenPluginsFolder();

    // Called by PluginLoader at startup: deletes any DLL or subfolder
    // with a matching .uninstall sentinel, then deletes the sentinel.
    void ProcessPendingUninstalls();
}
