using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AzureTray.Plugins;

// JSON-backed per-plugin config. Stores two things:
//   * which tenants are enabled for each plugin (default: all)
//   * generic option values declared via IPluginConfigurable
//
// Reads on startup, writes-through on every change. Lossy if the file is
// hand-edited to invalid JSON — we log and start from defaults rather than
// crashing the host.
public sealed class PluginConfigStore : IPluginConfigStore
{
    private readonly IAppPaths _paths;
    private readonly ILogger<PluginConfigStore> _logger;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, PluginConfigEntry> _entries
        = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public PluginConfigStore(IAppPaths paths, ILogger<PluginConfigStore> logger)
    {
        _paths = paths;
        _logger = logger;
        Load();
    }

    public event Action<string>? PluginConfigChanged;

    public bool IsTenantEnabledFor(string pluginId, string tenantId)
    {
        if (!_entries.TryGetValue(pluginId, out var entry)) return true;
        if (entry.DisabledTenants is null) return true;
        return !entry.DisabledTenants.Contains(tenantId);
    }

    public IReadOnlySet<string> GetDisabledTenants(string pluginId)
    {
        if (!_entries.TryGetValue(pluginId, out var entry) || entry.DisabledTenants is null)
        {
            return EmptySet;
        }
        return entry.DisabledTenants;
    }

    public void SetTenantEnabled(string pluginId, string tenantId, bool enabled)
    {
        var entry = _entries.GetOrAdd(pluginId, _ => new PluginConfigEntry());
        bool changed;
        lock (entry.Lock)
        {
            entry.DisabledTenants ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            changed = enabled
                ? entry.DisabledTenants.Remove(tenantId)
                : entry.DisabledTenants.Add(tenantId);
        }
        if (!changed) return;

        Persist();
        PluginConfigChanged?.Invoke(pluginId);
    }

    public IReadOnlyDictionary<string, object?> GetOptions(string pluginId)
    {
        if (!_entries.TryGetValue(pluginId, out var entry) || entry.Options is null)
        {
            return EmptyOptions;
        }
        lock (entry.Lock) return new Dictionary<string, object?>(entry.Options);
    }

    public void SetOption(string pluginId, string key, object? value)
    {
        var entry = _entries.GetOrAdd(pluginId, _ => new PluginConfigEntry());
        lock (entry.Lock)
        {
            entry.Options ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            entry.Options[key] = value;
        }
        Persist();
        PluginConfigChanged?.Invoke(pluginId);
    }

    private void Load()
    {
        if (!File.Exists(_paths.PluginConfigFilePath)) return;

        try
        {
            using var stream = File.OpenRead(_paths.PluginConfigFilePath);
            var dto = JsonSerializer.Deserialize<RootDto>(stream, JsonOptions);
            if (dto?.Plugins is null) return;

            foreach (var (pluginId, pluginDto) in dto.Plugins)
            {
                var entry = new PluginConfigEntry
                {
                    DisabledTenants = pluginDto.DisabledTenants is { Count: > 0 }
                        ? new HashSet<string>(pluginDto.DisabledTenants, StringComparer.OrdinalIgnoreCase)
                        : null,
                    Options = pluginDto.Options is { Count: > 0 }
                        ? UnboxJsonElements(pluginDto.Options)
                        : null,
                };
                _entries[pluginId] = entry;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load plugin config from {Path}; starting fresh.",
                _paths.PluginConfigFilePath);
        }
    }

    // System.Text.Json deserializes object? into JsonElement; flatten to
    // primitive CLR types so plugin consumers don't need to know the JSON
    // representation. Anything we don't recognize falls through as the
    // raw JsonElement so a future option kind can still read it.
    private static Dictionary<string, object?> UnboxJsonElements(IDictionary<string, object?> source)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, raw) in source)
        {
            result[key] = raw switch
            {
                JsonElement el => UnboxElement(el),
                _ => raw,
            };
        }
        return result;
    }

    private static object? UnboxElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt32(out var i) ? (object)i : el.GetDouble(),
        JsonValueKind.Null => null,
        _ => el,
    };

    private void Persist()
    {
        lock (_writeLock)
        {
            try
            {
                var dto = new RootDto
                {
                    Plugins = _entries.ToDictionary(
                        kvp => kvp.Key,
                        kvp =>
                        {
                            lock (kvp.Value.Lock)
                            {
                                return new PluginDto
                                {
                                    DisabledTenants = kvp.Value.DisabledTenants?.ToList(),
                                    Options = kvp.Value.Options is null
                                        ? null
                                        : new Dictionary<string, object?>(kvp.Value.Options),
                                };
                            }
                        }),
                };

                Directory.CreateDirectory(Path.GetDirectoryName(_paths.PluginConfigFilePath)!);
                using var stream = File.Create(_paths.PluginConfigFilePath);
                JsonSerializer.Serialize(stream, dto, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write plugin config to {Path}.",
                    _paths.PluginConfigFilePath);
            }
        }
    }

    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlyDictionary<string, object?> EmptyOptions =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    private sealed class PluginConfigEntry
    {
        public object Lock { get; } = new();
        public HashSet<string>? DisabledTenants { get; set; }
        public Dictionary<string, object?>? Options { get; set; }
    }

    private sealed class RootDto
    {
        public Dictionary<string, PluginDto>? Plugins { get; set; }
    }

    private sealed class PluginDto
    {
        public List<string>? DisabledTenants { get; set; }
        public Dictionary<string, object?>? Options { get; set; }
    }
}
