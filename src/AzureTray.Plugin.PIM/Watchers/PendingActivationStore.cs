using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.PIM.Watchers;

// Per-tenant record of activation requests the signed-in user submitted that are
// waiting on an approver. Persisted next to the eligible-role cache so an
// approval that lands while the app is closed is still detected on the next
// start. EligibleRolesWatcher writes entries; PendingActivationWatcher polls and
// removes them.
internal sealed class PendingActivationStore
{
    private readonly IPluginContext _context;
    private readonly PluginTenant _tenant;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingActivationRequest> _byRequestId
        = new(StringComparer.OrdinalIgnoreCase);

    public PendingActivationStore(IPluginContext context, PluginTenant tenant)
    {
        _context = context;
        _tenant = tenant;
        Load();
    }

    public IReadOnlyList<PendingActivationRequest> Current
    {
        get { lock (_gate) return _byRequestId.Values.ToArray(); }
    }

    public void Track(PendingActivationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId)) return;

        lock (_gate)
        {
            if (!_byRequestId.TryAdd(request.RequestId, request)) return;
        }

        _context.Logger.LogInformation(
            "Tracking pending PIM activation {RequestId} ({RoleName} on {Scope}) for tenant {TenantId}.",
            request.RequestId, request.RoleName, request.ScopeDisplay, _tenant.TenantId);
        Save();
    }

    public void StopTracking(string requestId)
    {
        lock (_gate)
        {
            if (!_byRequestId.Remove(requestId)) return;
        }
        Save();
    }

    // Drops entries older than maxAge so an approval that is never actioned
    // can't keep the file (and the poll) growing forever. Returns how many went.
    public int DropOlderThan(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        List<PendingActivationRequest> stale;
        lock (_gate)
        {
            stale = _byRequestId.Values.Where(r => r.SubmittedAt < cutoff).ToList();
            foreach (var entry in stale) _byRequestId.Remove(entry.RequestId);
        }

        if (stale.Count == 0) return 0;

        foreach (var entry in stale)
        {
            _context.Logger.LogInformation(
                "Stopped tracking PIM activation {RequestId} ({RoleName}) on tenant {TenantId}: no decision within {MaxAgeHours}h.",
                entry.RequestId, entry.RoleName, _tenant.TenantId, maxAge.TotalHours);
        }
        Save();
        return stale.Count;
    }

    private string StorePath =>
        Path.Combine(_context.DataDir, $"pending-activations-{Sanitize(_tenant.TenantId)}.json");

    private static string Sanitize(string s)
        => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            using var stream = File.OpenRead(StorePath);
            var entries = JsonSerializer.Deserialize<PendingActivationRequest[]>(stream);
            if (entries is null) return;

            lock (_gate)
            {
                foreach (var entry in entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry?.RequestId))
                    {
                        _byRequestId[entry!.RequestId] = entry;
                    }
                }
            }

            _context.Logger.LogInformation(
                "Loaded {Count} pending PIM activation(s) for tenant {TenantId}.",
                _byRequestId.Count, _tenant.TenantId);
        }
        catch (Exception ex)
        {
            // A legacy, truncated, or hand-edited file is treated as a cache
            // miss — worst case an approval that landed while the app was down
            // is only picked up by the next eligible-roles poll.
            _context.Logger.LogWarning(ex,
                "Pending-activation store load failed for tenant {TenantId}; starting empty.",
                _tenant.TenantId);
        }
    }

    private void Save()
    {
        PendingActivationRequest[] snapshot;
        lock (_gate) snapshot = _byRequestId.Values.ToArray();

        try
        {
            Directory.CreateDirectory(_context.DataDir);
            using var stream = File.Create(StorePath);
            JsonSerializer.Serialize(stream, snapshot);
        }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(ex,
                "Pending-activation store save failed for tenant {TenantId}; tracking is in-memory only this session.",
                _tenant.TenantId);
        }
    }
}
