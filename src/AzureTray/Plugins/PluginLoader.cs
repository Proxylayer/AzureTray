using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.AzureCloud;
using AzureTray.Configuration;
using AzureTray.Extensions;
using AzureTray.Plugin.Contracts;
using AzureTray.Tenants;

// Disambiguate from System.Windows.Forms.Timer, which is in scope in this
// WinForms-hosting assembly. The watcher debounce uses threadpool timers.
using Timer = System.Threading.Timer;

namespace AzureTray.Plugins;

public sealed class PluginLoader : IPluginLoader, IHostedService, IDisposable
{
    // Resolved once from the host assembly. Strips any '+build' metadata
    // suffix so plugins can pass it straight into System.Version.Parse
    // for capability comparisons.
    private static readonly string? HostVersion = ResolveHostVersion();

    private readonly IAppPaths _paths;
    private readonly IPluginSignatureVerifier _verifier;
    private readonly PluginOptions _options;
    private readonly IPluginHttpClientCore _http;
    private readonly INotifier _notifier;
    private readonly IClipboard _clipboard;
    private readonly ITenantStore _tenantStore;
    private readonly IAzureCloudConfig _cloud;
    private readonly IExtensionInstaller _installer;
    private readonly ITenantReadinessTracker _readiness;
    private readonly IPluginConfigStore _configStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginLoader> _logger;
    private readonly List<LoadedPluginEntry> _entries = new();
    private readonly Lock _entriesGate = new();

    // ─── Live reload (plugins-folder watcher) ────────────────────────────
    //
    // Watches PluginsDir so that dropping a new plugin .nupkg's DLLs over an
    // existing install hot-swaps the running instance with no app restart.
    // Copying a package fires a burst of Changed events (the plugin DLL plus
    // each transitive dep, often more than once), so reloads are debounced
    // per reload-target until the folder goes quiet. Host-initiated loads
    // (the Settings install commands) briefly suppress the watcher for the
    // affected folder so they don't double-reload on their own file writes.
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan HostReloadSuppression = TimeSpan.FromSeconds(5);

    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, Timer> _reloadDebounce =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _suppressedTargets =
        new(StringComparer.OrdinalIgnoreCase);

    internal PluginLoader(
        IAppPaths paths,
        IPluginSignatureVerifier verifier,
        IOptions<PluginOptions> options,
        IPluginHttpClientCore http,
        INotifier notifier,
        IClipboard clipboard,
        ITenantStore tenantStore,
        IAzureCloudConfig cloud,
        IExtensionInstaller installer,
        ITenantReadinessTracker readiness,
        IPluginConfigStore configStore,
        ILoggerFactory loggerFactory)
    {
        _paths = paths;
        _verifier = verifier;
        _options = options.Value;
        _http = http;
        _notifier = notifier;
        _clipboard = clipboard;
        _tenantStore = tenantStore;
        _cloud = cloud;
        _installer = installer;
        _readiness = readiness;
        _configStore = configStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginLoader>();
    }

    public IReadOnlyList<LoadedPlugin> LoadedPlugins
    {
        get
        {
            lock (_entriesGate)
            {
                return _entries.Select(e => e.Loaded).ToArray();
            }
        }
    }

    // Fire-and-forget: returning a completed Task immediately unblocks
    // host.Start() so the tray icon appears in ~1s instead of waiting on
    // signature verification, assembly load, and InitAsync of every
    // plugin (which JIT-compiles the plugin's dependency graph on first
    // run and can take 5-10+ seconds with multiple plugins). Plugins are
    // designed to handle late initialization — each replays already-ready
    // tenants by iterating ITenantReadinessTracker.ReadyTenants in its
    // InitAsync, so no events are lost. The PluginsChanged event fires
    // when each plugin finishes loading so the tray menu re-renders.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await LoadAllAsync(cancellationToken).ConfigureAwait(false);
                StartWatcher();
            }
            catch (OperationCanceledException) { /* host shutting down */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background plugin load failed.");
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        StopWatcher();
        await UnloadAllAsync(cancellationToken).ConfigureAwait(false);
    }

    // The folder watcher and its debounce timers are disposable; StopWatcher
    // is idempotent so disposing after StopAsync is harmless.
    public void Dispose() => StopWatcher();

    public event Action? PluginsChanged;

    public async Task LoadAllAsync(CancellationToken cancellationToken)
    {
        _installer.ProcessPendingUninstalls();

        if (!Directory.Exists(_paths.PluginsDir))
        {
            _logger.LogInformation(
                "Plugins directory {Dir} does not exist; no plugins to load.",
                _paths.PluginsDir);
            return;
        }

        var loadedAny = false;

        // (1) Back-compat: top-level *.dll files in plugins/ are treated
        //     as standalone plugins (the legacy "drop a DLL" path).
        foreach (var dllPath in Directory.EnumerateFiles(_paths.PluginsDir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryLoadAsync(dllPath, cancellationToken) is not null) loadedAny = true;
        }

        // (2) NuGet-installed layout: each plugin gets its own subfolder
        //     under plugins/, named for the package id. The plugin DLL
        //     itself is "{folder-name}.dll" by convention — the rest of
        //     the *.dll files in the subfolder are transitive deps that
        //     PluginLoadContext resolves via its sibling-DLL fallback.
        foreach (var subDir in Directory.EnumerateDirectories(_paths.PluginsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folderName = Path.GetFileName(subDir);
            var conventionalPath = Path.Combine(subDir, folderName + ".dll");
            if (File.Exists(conventionalPath))
            {
                if (await TryLoadAsync(conventionalPath, cancellationToken) is not null) loadedAny = true;
                continue;
            }

            // Convention miss: scan the folder for any DLL whose
            // assembly implements ITrayPlugin. TryLoadAsync filters
            // non-plugin DLLs out, but we don't want to load every dep
            // assembly into its own LoadContext — we only probe DLLs
            // whose file name doesn't look like a typical framework or
            // package dep (heuristic: skip anything beginning with
            // "System.", "Microsoft.", or matching the folder's own
            // dep-style prefix).
            foreach (var dllPath in Directory.EnumerateFiles(subDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(dllPath);
                if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Azure.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Newtonsoft.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (await TryLoadAsync(dllPath, cancellationToken) is not null)
                {
                    loadedAny = true;
                    break;  // one plugin per folder
                }
            }
        }

        if (loadedAny) PluginsChanged?.Invoke();
    }

    public async Task<LoadedPlugin?> LoadOneAsync(string dllPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);

        // A host-initiated load owns this target's file writes; keep the
        // watcher from reacting to them.
        SuppressWatcher(dllPath);

        // Idempotent: if a plugin from this file is already loaded, return it.
        lock (_entriesGate)
        {
            var existing = _entries.FirstOrDefault(e =>
                string.Equals(e.Loaded.AssemblyPath, dllPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing.Loaded;
        }

        var loaded = await TryLoadAsync(dllPath, cancellationToken).ConfigureAwait(false);
        if (loaded is not null) PluginsChanged?.Invoke();
        return loaded;
    }

    public async Task<bool> UnloadOneAsync(string pluginId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        LoadedPluginEntry? entry;
        lock (_entriesGate)
        {
            entry = _entries.FirstOrDefault(e =>
                string.Equals(e.Loaded.Plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (entry is null) return false;
            _entries.Remove(entry);
        }

        await UnloadEntryAsync(entry, pluginId, cancellationToken).ConfigureAwait(false);

        PluginsChanged?.Invoke();
        return true;
    }

    // Tears a single entry down: ShutdownAsync → dispose host context →
    // unload the collectible ALC → force a GC so the context releases its
    // (memory-mapped only if some dep used LoadFromAssemblyPath) handles and
    // any disposable plugin state is finalized. Does NOT touch _entries or
    // raise PluginsChanged — callers own list mutation + eventing so a reload
    // emits a single change notification.
    private async Task UnloadEntryAsync(LoadedPluginEntry entry, string pluginId, CancellationToken cancellationToken)
    {
        try { await entry.Loaded.Plugin.ShutdownAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin {Id} threw during shutdown.", pluginId);
        }

        entry.Context.Dispose();

        try { entry.LoadContext.Unload(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unload AssemblyLoadContext for {Id}.", pluginId);
        }

        // Force GC so the collectible context releases the file handle. This is
        // best-effort: a caller that wants to overwrite the DLL afterward
        // should still tolerate a transient lock.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // Hot-swap to a new version: shut down the running instance (by id), then
    // load the DLL now sitting at dllPath into a fresh collectible context and
    // re-run InitializeAsync. Use after the file on disk has been replaced.
    // Raises a single PluginsChanged after the swap. Returns the new
    // LoadedPlugin, or null if the new bytes failed to load (in which case the
    // plugin ends up unloaded — the old version is already gone).
    public async Task<LoadedPlugin?> ReloadOneAsync(string pluginId, string dllPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);

        // Suppress the watcher for this target before we touch files/state so
        // the install's own writes (or this reload's) don't bounce back in.
        SuppressWatcher(dllPath);

        LoadedPluginEntry? entry;
        lock (_entriesGate)
        {
            entry = _entries.FirstOrDefault(e =>
                string.Equals(e.Loaded.Plugin.Id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) _entries.Remove(entry);
        }

        if (entry is not null)
        {
            await UnloadEntryAsync(entry, pluginId, cancellationToken).ConfigureAwait(false);
        }

        var loaded = await TryLoadAsync(dllPath, cancellationToken).ConfigureAwait(false);
        PluginsChanged?.Invoke();
        return loaded;
    }

    // Load dllPath, or — if a plugin is already loaded from that exact path —
    // reload it (hot-swap to the new bytes). This is what the install/update
    // commands and the folder watcher call: it does the right thing whether
    // the plugin is brand-new or an in-place version bump.
    public async Task<LoadedPlugin?> LoadOrReloadAsync(string dllPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);

        string? loadedId;
        lock (_entriesGate)
        {
            loadedId = _entries.FirstOrDefault(e =>
                string.Equals(e.Loaded.AssemblyPath, dllPath, StringComparison.OrdinalIgnoreCase))
                ?.Loaded.Plugin.Id;
        }

        return loadedId is not null
            ? await ReloadOneAsync(loadedId, dllPath, cancellationToken).ConfigureAwait(false)
            : await LoadOneAsync(dllPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnloadAllAsync(CancellationToken cancellationToken)
    {
        LoadedPluginEntry[] snapshot;
        lock (_entriesGate)
        {
            snapshot = _entries.ToArray();
            _entries.Clear();
        }

        foreach (var entry in snapshot)
        {
            try
            {
                await entry.Loaded.Plugin.ShutdownAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Id} threw during shutdown.", entry.Loaded.Plugin.Id);
            }

            entry.Context.Dispose();

            try
            {
                entry.LoadContext.Unload();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to unload plugin context for {Id}.", entry.Loaded.Plugin.Id);
            }
        }
    }

    private async Task<LoadedPlugin?> TryLoadAsync(string dllPath, CancellationToken cancellationToken)
    {
        SignatureVerdict verdict;
        try
        {
            verdict = _verifier.Verify(dllPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signature verification crashed for {Path}; skipping.", dllPath);
            return null;
        }

        if (!IsTrusted(verdict))
        {
            _logger.LogWarning(
                "Plugin {Path} rejected by trust mode {TrustMode}: signed={IsSigned}, thumbprint={Thumbprint}.",
                dllPath, _options.TrustMode, verdict.IsSigned, verdict.SignerThumbprint);
            return null;
        }

        PluginLoadContext? loadContext = null;
        try
        {
            loadContext = new PluginLoadContext(dllPath);
            // Load via byte buffer (LoadFromStream) instead of
            // LoadFromAssemblyPath so the OS never opens an mmap on the
            // DLL — uninstall/upgrade can then delete or overwrite the
            // file immediately, without waiting for the collectible
            // load context to finish unloading.
            var assembly = PluginLoadContext.LoadFromBytes(loadContext, dllPath);

            var pluginType = assembly.GetTypes().FirstOrDefault(t =>
                typeof(ITrayPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

            if (pluginType is null)
            {
                _logger.LogWarning("Plugin {Path} contains no ITrayPlugin implementation.", dllPath);
                loadContext.Unload();
                return null;
            }

            if (Activator.CreateInstance(pluginType) is not ITrayPlugin plugin)
            {
                _logger.LogWarning("Plugin {Path} type {Type} could not be activated as ITrayPlugin.", dllPath, pluginType.FullName);
                loadContext.Unload();
                return null;
            }

            if (!PluginApiVersion.IsSupported(plugin.ApiVersion))
            {
                _logger.LogWarning(
                    "Plugin {Id} declares ApiVersion {Declared}; host supports [{Min}, {Max}]. Skipping.",
                    plugin.Id, plugin.ApiVersion, PluginApiVersion.MinSupported, PluginApiVersion.Current);
                loadContext.Unload();
                return null;
            }

            var pluginLogger = _loggerFactory.CreateLogger($"Plugin.{plugin.Id}");
            var tenantSnapshot = _tenantStore.GetAll()
                .Select(t => new PluginTenant(t.TenantId, t.DisplayName))
                .ToArray();

            // Create the plugin's data dir under PluginDataRoot. Sanitize id
            // just in case — plugin authors typically use reverse-DNS so this
            // is defensive against future plugins that pick odd ids.
            var safeId = string.Join("_", plugin.Id.Split(Path.GetInvalidFileNameChars()));
            var pluginDataDir = Path.Combine(_paths.PluginDataRoot, safeId);
            Directory.CreateDirectory(pluginDataDir);

            var context = new PluginContext(
                plugin.Id,
                pluginLogger,
                _http,
                _notifier,
                _clipboard,
                tenantSnapshot,
                _readiness,
                _configStore,
                _cloud.GraphScope,
                _cloud.ArmScope,
                pluginDataDir,
                HostVersion);

            try
            {
                await plugin.InitializeAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Id} threw during InitializeAsync; not registering.", plugin.Id);
                context.Dispose();
                loadContext.Unload();
                return null;
            }

            // Hydrate IPluginConfigurable values from the store BEFORE the plugin
            // can start using them. We pull only keys already persisted so the
            // plugin's defaults still drive first-run behavior.
            if (plugin is IPluginConfigurable configurable)
            {
                var stored = _configStore.GetOptions(plugin.Id);
                foreach (var option in configurable.Options)
                {
                    if (stored.TryGetValue(option.Key, out var value))
                    {
                        try { configurable.SetValue(option.Key, CoerceValue(value, option.Kind)); }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Plugin {Id} rejected stored value for {Key}.",
                                plugin.Id, option.Key);
                        }
                    }
                }

                // Plugin-side edits write through to the store. Wrap in a
                // re-entrance guard so persisting → store change → plugin
                // re-read can't loop back into a write.
                var pluginIdCaptured = plugin.Id;
                configurable.ValuesChanged += () =>
                {
                    try
                    {
                        foreach (var option in configurable.Options)
                        {
                            if (configurable.Values.TryGetValue(option.Key, out var v))
                            {
                                _configStore.SetOption(pluginIdCaptured, option.Key, v);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to persist values for plugin {Id}.", pluginIdCaptured);
                    }
                };
            }

            var loadedPlugin = new LoadedPlugin(plugin, dllPath, verdict);
            var entry = new LoadedPluginEntry(loadedPlugin, loadContext, context);
            lock (_entriesGate)
            {
                _entries.Add(entry);
            }

            _logger.LogInformation(
                "Loaded plugin {Id} {DisplayName} v{Version} from {Path}.",
                plugin.Id, plugin.DisplayName, plugin.Version, dllPath);
            return loadedPlugin;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load plugin from {Path}.", dllPath);
            loadContext?.Unload();
            return null;
        }
    }

    private bool IsTrusted(SignatureVerdict verdict) => _options.TrustMode switch
    {
        PluginTrustMode.AllowUnsigned => true,
        PluginTrustMode.RequireSigned => verdict.IsSigned,
        PluginTrustMode.RequireTrustedPublisher =>
            verdict.IsSigned
            && verdict.SignerThumbprint is not null
            && _options.TrustedPublisherThumbprints.Contains(verdict.SignerThumbprint, StringComparer.OrdinalIgnoreCase),
        _ => false,
    };

    // Coerces a JSON-deserialized value into the type the plugin declared.
    // Without this, plugins that declared Integer would see a double or a
    // JsonElement on first load.
    private static object? CoerceValue(object? value, PluginOptionKind kind)
    {
        if (value is null) return null;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return kind switch
        {
            PluginOptionKind.Boolean => Convert.ToBoolean(value, invariant),
            PluginOptionKind.Number => Convert.ToInt32(value, invariant),
            PluginOptionKind.Text => Convert.ToString(value, invariant),
            _ => value,
        };
    }

    private static string? ResolveHostVersion()
    {
        var asm = typeof(PluginLoader).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            // SourceLink appends '+commitSha' for diagnostic provenance; the
            // SemVer prefix is what callers actually want for version compares.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }
        return asm.GetName().Version?.ToString();
    }

    // ─── Folder watcher ───────────────────────────────────────────────────

    private void StartWatcher()
    {
        if (_watcher is not null) return;
        try
        {
            Directory.CreateDirectory(_paths.PluginsDir);
            _watcher = new FileSystemWatcher(_paths.PluginsDir, "*.dll")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                // Big packages drop many DLLs at once; a generous buffer
                // avoids InternalBufferOverflow dropping events.
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnPluginFileChanged;
            _watcher.Created += OnPluginFileChanged;
            _watcher.Renamed += (s, e) => OnPluginFileChanged(s, e);
            _watcher.Error += (_, e) =>
                _logger.LogWarning(e.GetException(), "Plugin folder watcher error; live reload may miss a change.");
            _watcher.EnableRaisingEvents = true;
            _logger.LogInformation("Watching {Dir} for plugin updates (live reload enabled).", _paths.PluginsDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start the plugin folder watcher; live reload on file change is disabled.");
        }
    }

    private void StopWatcher()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch { /* best effort on shutdown */ }

        foreach (var kvp in _reloadDebounce)
        {
            kvp.Value.Dispose();
        }
        _reloadDebounce.Clear();
    }

    private void OnPluginFileChanged(object sender, FileSystemEventArgs e)
    {
        var target = ResolveReloadTarget(e.FullPath);
        if (target is null) return;
        if (IsSuppressed(target)) return;

        // Coalesce the burst of writes from a package copy into one reload,
        // fired once the target has been quiet for WatcherDebounce.
        var timer = _reloadDebounce.GetOrAdd(target, key =>
            new Timer(OnDebounceElapsed, key, Timeout.Infinite, Timeout.Infinite));
        try { timer.Change(WatcherDebounce, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* racing shutdown */ }
    }

    private void OnDebounceElapsed(object? state)
    {
        var target = (string)state!;
        if (_reloadDebounce.TryRemove(target, out var timer)) timer.Dispose();
        if (IsSuppressed(target)) return;
        _ = ReloadTargetAsync(target);
    }

    private async Task ReloadTargetAsync(string target)
    {
        try
        {
            var pluginsDir = Path.GetFullPath(_paths.PluginsDir).TrimEnd(Path.DirectorySeparatorChar);
            var targetParent = Path.GetDirectoryName(target)?.TrimEnd(Path.DirectorySeparatorChar);

            string? dllPath;
            if (string.Equals(targetParent, pluginsDir, StringComparison.OrdinalIgnoreCase)
                && target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // Top-level legacy layout: the target IS the plugin DLL.
                dllPath = File.Exists(target) ? target : null;
            }
            else
            {
                // Subfolder layout: prefer the exact assembly of a plugin
                // already loaded from this folder — handles packages whose DLL
                // name differs from the folder/package id. Fall back to the
                // conventional <folder>/<folder>.dll for a brand-new drop.
                lock (_entriesGate)
                {
                    dllPath = _entries
                        .Select(en => en.Loaded.AssemblyPath)
                        .FirstOrDefault(p => IsUnderFolder(p, target));
                }
                if (dllPath is null)
                {
                    var conventional = Path.Combine(target, Path.GetFileName(target) + ".dll");
                    dllPath = File.Exists(conventional) ? conventional : null;
                }
            }

            if (dllPath is null) return;

            if (!await WaitForReadableAsync(dllPath, CancellationToken.None).ConfigureAwait(false))
            {
                _logger.LogWarning("Plugin file {Path} never became readable; skipping watch reload.", dllPath);
                return;
            }

            _logger.LogInformation("Detected plugin change in {Target}; reloading.", Path.GetFileName(target));
            await LoadOrReloadAsync(dllPath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watch-triggered reload failed for {Target}.", target);
        }
    }

    // Maps any file path under PluginsDir to its "reload unit": the plugin's
    // subfolder (modern layout) or the DLL itself (top-level legacy). Returns
    // null for paths outside PluginsDir. Used both to key the debounce/
    // suppression maps and to figure out what to reload.
    private string? ResolveReloadTarget(string fullPath)
    {
        try
        {
            var pluginsDir = Path.GetFullPath(_paths.PluginsDir).TrimEnd(Path.DirectorySeparatorChar);
            var dir = Path.GetDirectoryName(Path.GetFullPath(fullPath))?.TrimEnd(Path.DirectorySeparatorChar);
            if (dir is null) return null;

            if (string.Equals(dir, pluginsDir, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(fullPath);   // top-level: the file is the unit
            }

            var folder = dir;
            while (true)
            {
                var parent = Path.GetDirectoryName(folder)?.TrimEnd(Path.DirectorySeparatorChar);
                if (parent is null) return null;     // not under PluginsDir
                if (string.Equals(parent, pluginsDir, StringComparison.OrdinalIgnoreCase)) return folder;
                folder = parent;
            }
        }
        catch { return null; }
    }

    private static bool IsUnderFolder(string path, string folder)
    {
        try
        {
            var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            return p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // A freshly-copied DLL may still be open for writing when the first
    // Changed event fires. Poll until it opens for shared read, or give up.
    private static async Task<bool> WaitForReadableAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
        return false;
    }

    private void SuppressWatcher(string anyPathInTarget)
    {
        var target = ResolveReloadTarget(anyPathInTarget);
        if (target is null) return;
        _suppressedTargets[target] = DateTime.UtcNow.Add(HostReloadSuppression);
    }

    private bool IsSuppressed(string target)
    {
        if (_suppressedTargets.TryGetValue(target, out var until))
        {
            if (DateTime.UtcNow < until) return true;
            _suppressedTargets.TryRemove(target, out _);
        }
        return false;
    }

    private sealed record LoadedPluginEntry(
        LoadedPlugin Loaded,
        PluginLoadContext LoadContext,
        PluginContext Context);
}
