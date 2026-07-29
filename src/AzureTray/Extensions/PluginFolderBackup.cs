using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AzureTray.Extensions;

// A throwaway copy of plugins/<packageId>/ taken before an in-place version
// bump, so a refused or failed update leaves the working version installed.
//
// Why this exists: installing an update writes the new DLLs over the existing
// ones, and the pre-existing rollback for a declined install deletes the
// files it just wrote — which for a subfolder plugin means deleting the whole
// folder. On a fresh install that's correct; on an update it would silently
// uninstall a plugin the user was happily running because they said "no" to
// an unsigned prompt.
//
// The copy lives under the OS temp folder rather than inside plugins/ so the
// loader's FileSystemWatcher never sees it.
internal sealed class PluginFolderBackup : IDisposable
{
    private readonly string _sourceDir;
    private readonly string _backupDir;
    private readonly ILogger _logger;
    private bool _disposed;

    private PluginFolderBackup(string sourceDir, string backupDir, ILogger logger)
    {
        _sourceDir = sourceDir;
        _backupDir = backupDir;
        _logger = logger;
    }

    // Snapshots the per-plugin folder containing `installedDllPath`. Returns
    // null — and logs — when there's nothing safe to snapshot (legacy
    // top-level DLL, folder missing) or when the copy fails; callers then
    // behave exactly as they did before backups existed.
    public static PluginFolderBackup? TryCreate(string installedDllPath, string packageId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(installedDllPath) || string.IsNullOrWhiteSpace(packageId)) return null;

        var sourceDir = Path.GetDirectoryName(Path.GetFullPath(installedDllPath));
        if (string.IsNullOrEmpty(sourceDir) || !Directory.Exists(sourceDir)) return null;

        // Only the per-plugin layout (plugins/<packageId>/<packageId>.dll) is
        // snapshotable: a legacy top-level DLL sits directly in the plugins
        // root, which must never be copied or deleted wholesale.
        if (!string.Equals(Path.GetFileName(sourceDir), packageId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var backupDir = Path.Combine(
            Path.GetTempPath(),
            "AzureTray.plugin-backup",
            Guid.NewGuid().ToString("N"));

        try
        {
            CopyDirectory(sourceDir, backupDir);
            return new PluginFolderBackup(sourceDir, backupDir, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not snapshot {SourceDir} before updating; a failed update will not be rolled back.",
                sourceDir);
            return null;
        }
    }

    // Puts the snapshot back over the live folder. Best-effort: a failure here
    // is logged and reported, never thrown, because it runs on paths that are
    // already handling another failure.
    public bool TryRestore()
    {
        try
        {
            if (Directory.Exists(_sourceDir))
            {
                Directory.Delete(_sourceDir, recursive: true);
            }
            CopyDirectory(_backupDir, _sourceDir);
            _logger.LogInformation("Restored {SourceDir} from the pre-update snapshot.", _sourceDir);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to restore {SourceDir} from the pre-update snapshot at {BackupDir}. The plugin folder may be in a mixed state; reinstall the plugin from Settings.",
                _sourceDir, _backupDir);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clean up the plugin snapshot at {BackupDir}.", _backupDir);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            File.Copy(file, target, overwrite: true);
        }
    }
}
