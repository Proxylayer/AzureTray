using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AzureTray.Extensions;

// Process-wide "which plugin updates do we know about" snapshot. The poll
// loop publishes into it; the Settings window (a transient view model that
// comes and goes) reads Available on construction and subscribes to Changed,
// exactly as it does for IUpdateService.PendingUpdateVersion.
//
// Deliberately no interface and no dependencies: it holds a list and raises
// an event, and there is only ever one way to do that.
public sealed class PluginUpdateState
{
    private IReadOnlyList<PluginUpdate> _available = Array.Empty<PluginUpdate>();

    // Latest known set of available plugin updates. Empty when none.
    public IReadOnlyList<PluginUpdate> Available => Volatile.Read(ref _available);

    // Raised when the set changes. Subscribers may be called on any thread;
    // marshal to the WPF dispatcher before touching bindings.
    public event Action<IReadOnlyList<PluginUpdate>>? Changed;

    // Replaces the snapshot. No-ops (and raises nothing) when the new set
    // names the same package/version pairs as the current one, so an hourly
    // poll that keeps finding the same update doesn't churn the UI.
    public void Publish(IReadOnlyList<PluginUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var snapshot = updates.ToArray();
        if (SameSet(Available, snapshot)) return;

        Volatile.Write(ref _available, snapshot);
        Changed?.Invoke(snapshot);
    }

    // Drops a single plugin from the snapshot — used right after an update is
    // applied so the banner and the row button clear without waiting for the
    // next poll.
    public void Remove(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return;

        var remaining = Available
            .Where(u => !string.Equals(u.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Publish(remaining);
    }

    private static bool SameSet(IReadOnlyList<PluginUpdate> left, PluginUpdate[] right)
    {
        if (left.Count != right.Length) return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].PackageId, right[i].PackageId, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(left[i].LatestVersion, right[i].LatestVersion, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }
}
