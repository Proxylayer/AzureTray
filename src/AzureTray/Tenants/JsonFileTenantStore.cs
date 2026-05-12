using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Models;

namespace AzureTray.Tenants;

public sealed class JsonFileTenantStore : ITenantStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IAppPaths _paths;
    private readonly ILogger<JsonFileTenantStore> _logger;
    private readonly Dictionary<string, Tenant> _byTenantId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _stateGate = new();
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private bool _disposed;

    public JsonFileTenantStore(IAppPaths paths, ILogger<JsonFileTenantStore> logger)
    {
        _paths = paths;
        _logger = logger;
        LoadFromFile();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _ioGate.Dispose();
        _disposed = true;
    }

    public IReadOnlyList<Tenant> GetAll()
    {
        lock (_stateGate)
        {
            return _byTenantId.Values.ToArray();
        }
    }

    public Tenant? FindByTenantId(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        lock (_stateGate)
        {
            return _byTenantId.TryGetValue(tenantId, out var tenant) ? tenant : null;
        }
    }

    public async Task AddOrUpdateAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.TenantId);

        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateGate)
            {
                _byTenantId[tenant.TenantId] = tenant;
            }
            await WriteAsync(cancellationToken);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task RemoveAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            bool removed;
            lock (_stateGate)
            {
                removed = _byTenantId.Remove(tenantId);
            }
            if (removed)
            {
                await WriteAsync(cancellationToken);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_paths.TenantStoreFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_paths.TenantStoreFilePath);
            var doc = JsonSerializer.Deserialize<TenantStoreDocument>(json, JsonOptions);
            if (doc?.Tenants is null) return;

            lock (_stateGate)
            {
                foreach (var entry in doc.Tenants)
                {
                    if (!string.IsNullOrWhiteSpace(entry.TenantId))
                    {
                        _byTenantId[entry.TenantId] = entry;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse tenant store at {Path}; starting empty.", _paths.TenantStoreFilePath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read tenant store at {Path}; starting empty.", _paths.TenantStoreFilePath);
        }
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        Tenant[] snapshot;
        lock (_stateGate)
        {
            snapshot = _byTenantId.Values.ToArray();
        }

        var dir = Path.GetDirectoryName(_paths.TenantStoreFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(new TenantStoreDocument(snapshot.ToList()), JsonOptions);
        var tempPath = _paths.TenantStoreFilePath + ".tmp";

        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, _paths.TenantStoreFilePath, overwrite: true);
    }

    private sealed record TenantStoreDocument(List<Tenant> Tenants);
}
