using System;
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

namespace AzureTray.Plugins;

public sealed class PluginLoader : IPluginLoader, IHostedService
{
    // Resolved once from the host assembly. Strips any '+build' metadata
    // suffix so plugins can pass it straight into System.Version.Parse
    // for capability comparisons.
    private static readonly string? HostVersion = ResolveHostVersion();

    private readonly IAppPaths _paths;
    private readonly IPluginSignatureVerifier _verifier;
    private readonly PluginOptions _options;
    private readonly IPluginHttpClient _http;
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

    public PluginLoader(
        IAppPaths paths,
        IPluginSignatureVerifier verifier,
        IOptions<PluginOptions> options,
        IPluginHttpClient http,
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
            }
            catch (OperationCanceledException) { /* host shutting down */ }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background plugin load failed.");
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => UnloadAllAsync(cancellationToken);

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

        PluginsChanged?.Invoke();
        return true;
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

            if (plugin.ApiVersion != PluginApiVersion.Current)
            {
                _logger.LogWarning(
                    "Plugin {Id} declares ApiVersion {Declared}; host supports {Supported}. Skipping.",
                    plugin.Id, plugin.ApiVersion, PluginApiVersion.Current);
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

    private sealed record LoadedPluginEntry(
        LoadedPlugin Loaded,
        PluginLoadContext LoadContext,
        PluginContext Context);
}
