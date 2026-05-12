using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace AzureTray.Extensions;

public sealed class ExtensionInstaller : IExtensionInstaller
{
    public const string HttpClientName = "plugin-download";

    private const string UninstallSuffix = ".uninstall";

    // TFMs we'll accept from a .nupkg's lib/<tfm>/ folder, ordered most
    // to least preferred. Plugins ship against net8.0; netstandard2.0 is
    // an acceptable fallback because anything compiled to that runs on
    // net8.0 too.
    private static readonly string[] AcceptedTfms = ["net8.0", "netstandard2.1", "netstandard2.0"];

    // Short retry budget for hot deletes: the collectible ALC may still
    // hold the file handle for a few ms after Unload() until finalizers
    // run, even with GC.WaitForPendingFinalizers.
    private static readonly TimeSpan[] DeleteRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(400),
    ];

    private readonly IAppPaths _paths;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ExtensionInstaller> _logger;

    public ExtensionInstaller(IAppPaths paths, IHttpClientFactory httpFactory, ILogger<ExtensionInstaller> logger)
    {
        _paths = paths;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> InstallFromFileAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Plugin package not found.", sourcePath);
        }
        if (!string.Equals(Path.GetExtension(sourcePath), ".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Plugin file must have a .nupkg extension.");
        }

        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);

        // For local installs the registry-supplied packageId isn't around,
        // so we read it from the package's .nuspec. That's authoritative.
        var packageId = ReadPackageIdFromNuspec(archive)
            ?? throw new InvalidOperationException(
                "Could not determine plugin id: the .nupkg is missing a valid .nuspec/<id> entry.");

        return ExtractDllsFromNupkg(archive, packageId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> InstallFromUrlAsync(
        string packageId,
        string downloadUrl,
        string? checksumSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUrl);

        // Buffer the download in-memory: plugin packages are KB-MB
        // scale and we need a seekable stream for ZipArchive +
        // optional hashing.
        var client = _httpFactory.CreateClient(HttpClientName);
        byte[] bytes;
        using (var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(checksumSha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var expected = checksumSha256.Trim().ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Checksum mismatch for {packageId}: registry expected sha256={expected} but downloaded bytes are sha256={actual}. Install aborted.");
            }
        }

        using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        return ExtractDllsFromNupkg(archive, packageId, cancellationToken);
    }

    private List<string> ExtractDllsFromNupkg(ZipArchive archive, string packageId, CancellationToken cancellationToken)
    {
        // Per-plugin subfolder isolates each plugin's deps. PluginLoader
        // discovers plugins in plugins/<id>/<id>.dll (preferred) and
        // PluginLoadContext resolves transitive DLLs from the same dir.
        var targetDir = Path.Combine(_paths.PluginsDir, packageId);
        Directory.CreateDirectory(targetDir);

        var chosenTfm = ChooseBestTfm(archive)
            ?? throw new InvalidOperationException(
                $"Package '{packageId}' does not contain a supported lib/<tfm>/ folder " +
                $"(expected one of: {string.Join(", ", AcceptedTfms)}).");

        var prefix = $"lib/{chosenTfm}/";
        var installed = new List<string>();

        foreach (var entry in archive.Entries)
        {
            // Top-level .dll files inside the chosen lib folder. Accept
            // any DLL here (not just the main one) so plugin authors who
            // bundle transitive deps via <CopyLocalLockFileAssemblies>
            // get them installed alongside the plugin.
            if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = entry.FullName[prefix.Length..];
            if (relative.Length == 0 || relative.Contains('/') || relative.Contains('\\')) continue;
            if (!relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;

            installed.Add(WriteEntry(entry, targetDir, relative, cancellationToken));
        }

        if (installed.Count == 0)
        {
            throw new InvalidOperationException(
                $"Plugin package '{packageId}' contained no DLLs under lib/{chosenTfm}/.");
        }

        _logger.LogInformation(
            "Installed {Count} DLL(s) from nupkg into {TargetDir} (tfm={Tfm}).",
            installed.Count, targetDir, chosenTfm);
        return installed;
    }

    private static string WriteEntry(ZipArchiveEntry entry, string targetDir, string relative, CancellationToken cancellationToken)
    {
        var targetPath = Path.Combine(targetDir, relative);
        using (var entryStream = entry.Open())
        using (var dst = File.Create(targetPath))
        {
            entryStream.CopyTo(dst);
        }
        cancellationToken.ThrowIfCancellationRequested();

        // Clear any stale pending-uninstall sentinel — a fresh install supersedes it.
        var sentinel = targetPath + UninstallSuffix;
        if (File.Exists(sentinel)) File.Delete(sentinel);
        return targetPath;
    }

    private static string? ReadPackageIdFromNuspec(ZipArchive archive)
    {
        // .nuspec lives at the archive root, exactly one per package.
        var nuspec = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.Contains('/') &&
            !e.FullName.Contains('\\'));
        if (nuspec is null) return null;

        try
        {
            using var stream = nuspec.Open();
            var doc = XDocument.Load(stream);
            // The nuspec namespace varies (2010, 2011, 2013…); match by local name.
            var id = doc.Root?
                .Descendants()
                .FirstOrDefault(el => string.Equals(el.Name.LocalName, "id", StringComparison.OrdinalIgnoreCase))
                ?.Value
                ?.Trim();
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch
        {
            return null;
        }
    }

    private static string? ChooseBestTfm(ZipArchive archive)
    {
        // Collect the set of lib/<tfm>/ folders the package actually has.
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = entry.FullName["lib/".Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0) continue;
            available.Add(rest[..slash]);
        }

        foreach (var preferred in AcceptedTfms)
        {
            if (available.Contains(preferred)) return preferred;
        }
        return null;
    }

    public async Task<bool> TryDeleteAsync(string installedDllPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedDllPath);

        var pluginsDirFull = Path.GetFullPath(_paths.PluginsDir);
        var requested = Path.GetFullPath(installedDllPath);
        if (!requested.StartsWith(pluginsDirFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot delete a path outside the plugins folder: {installedDllPath}");
        }

        // Subfolder layout (plugins/<id>/<id>.dll) → wipe the whole folder
        // so bundled transitive deps go too. Top-level layout
        // (plugins/<id>.dll, legacy) → just the file.
        var parent = Path.GetDirectoryName(requested);
        var target = parent is not null && !string.Equals(
                Path.GetFullPath(parent).TrimEnd('\\'),
                pluginsDirFull.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase)
            ? parent
            : requested;

        // Force a finalizer pass so any plugin ALC that just unloaded
        // releases its mmap before our first delete attempt.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        for (var attempt = 0; attempt < DeleteRetryDelays.Length + 1; attempt++)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                }
                _logger.LogInformation("Hot-uninstalled extension at {Target}.", target);
                return true;
            }
            catch (IOException) when (attempt < DeleteRetryDelays.Length)
            {
                await Task.Delay(DeleteRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < DeleteRetryDelays.Length)
            {
                await Task.Delay(DeleteRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex,
                    "Hot delete still blocked for {Target} after {Attempts} attempts; falling back to startup cleanup.",
                    target, DeleteRetryDelays.Length + 1);
                return false;
            }
        }

        return false;
    }

    public Task RequestUninstallAsync(string installedDllPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedDllPath);

        var pluginsDirFull = Path.GetFullPath(_paths.PluginsDir);
        var requested = Path.GetFullPath(installedDllPath);
        if (!requested.StartsWith(pluginsDirFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot mark a path outside the plugins folder for uninstall: {installedDllPath}");
        }

        // Place the sentinel beside what we'll eventually clean up.
        var parent = Path.GetDirectoryName(requested);
        var isSubfolder = parent is not null && !string.Equals(
            Path.GetFullPath(parent).TrimEnd('\\'),
            pluginsDirFull.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

        var sentinel = isSubfolder
            ? Path.Combine(parent!, UninstallSuffix.TrimStart('.'))   // plugins/<id>/uninstall
            : requested + UninstallSuffix;                            // plugins/<id>.dll.uninstall

        File.WriteAllText(sentinel, string.Empty);

        _logger.LogInformation(
            "Marked extension {Path} for uninstall on next startup (sentinel={Sentinel}).",
            requested, sentinel);

        return Task.CompletedTask;
    }

    public IReadOnlyList<string> ListInstalledDlls()
    {
        if (!Directory.Exists(_paths.PluginsDir)) return Array.Empty<string>();

        var results = new List<string>();

        // Legacy: a plugin DLL sitting directly in plugins/.
        results.AddRange(Directory.EnumerateFiles(_paths.PluginsDir, "*.dll", SearchOption.TopDirectoryOnly));

        // Modern: subfolder layout plugins/<id>/<id>.dll. We surface the
        // <id>.dll only — bundled transitive deps shouldn't appear as
        // separate "installed extensions" rows.
        foreach (var dir in Directory.EnumerateDirectories(_paths.PluginsDir))
        {
            var folderName = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(folderName)) continue;
            var primary = Path.Combine(dir, folderName + ".dll");
            if (File.Exists(primary)) results.Add(primary);
        }

        return results;
    }

    public IReadOnlyList<string> ListPendingUninstalls()
    {
        if (!Directory.Exists(_paths.PluginsDir)) return Array.Empty<string>();

        var names = new List<string>();

        // Top-level legacy sentinels: plugins/<X.dll>.uninstall
        names.AddRange(
            Directory.EnumerateFiles(_paths.PluginsDir, "*.dll" + UninstallSuffix, SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n![..^UninstallSuffix.Length])!);

        // Subfolder sentinels: plugins/<id>/uninstall → report as "<id>.dll"
        var bareSentinelName = UninstallSuffix.TrimStart('.');
        foreach (var dir in Directory.EnumerateDirectories(_paths.PluginsDir))
        {
            if (File.Exists(Path.Combine(dir, bareSentinelName)))
            {
                var folder = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(folder)) names.Add(folder + ".dll");
            }
        }

        return names;
    }

    public void OpenPluginsFolder()
    {
        Directory.CreateDirectory(_paths.PluginsDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = _paths.PluginsDir,
            UseShellExecute = true,
        });
    }

    public void ProcessPendingUninstalls()
    {
        if (!Directory.Exists(_paths.PluginsDir)) return;

        // Top-level legacy: <name>.dll.uninstall pairs with <name>.dll.
        foreach (var sentinel in Directory.EnumerateFiles(_paths.PluginsDir, "*" + UninstallSuffix, SearchOption.TopDirectoryOnly))
        {
            var dllPath = sentinel[..^UninstallSuffix.Length];
            try
            {
                if (File.Exists(dllPath)) File.Delete(dllPath);
                File.Delete(sentinel);
                _logger.LogInformation("Uninstalled extension DLL: {Path}", dllPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to complete uninstall for {Path}; will retry next launch.", dllPath);
            }
        }

        // Subfolder layout: plugins/<id>/uninstall → wipe plugins/<id>/.
        var bareSentinelName = UninstallSuffix.TrimStart('.');
        foreach (var dir in Directory.EnumerateDirectories(_paths.PluginsDir))
        {
            if (!File.Exists(Path.Combine(dir, bareSentinelName))) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                _logger.LogInformation("Uninstalled extension folder: {Path}", dir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to complete uninstall for {Path}; will retry next launch.", dir);
            }
        }
    }
}
