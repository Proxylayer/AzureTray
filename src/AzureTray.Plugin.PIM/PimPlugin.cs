using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Permissions;
using AzureTray.Plugin.PIM.Watchers;

namespace AzureTray.Plugin.PIM;

public sealed class PimPlugin : ITrayPlugin, IMenuChangeNotifier, IBadgeProvider, IPluginTestProvider, IDisposable
{
    public event Action? MenuChanged;
    public event Action? BadgeChanged;

    // Pending approvals turn the tray icon orange. Counts roll up across all
    // tracked tenants. Zero approvals → BadgeState.Normal.
    public BadgeState State => Count > 0 ? BadgeState.Pending : BadgeState.Normal;
    public int Count
    {
        get
        {
            var total = 0;
            foreach (var watcher in PendingWatchersSnapshot())
            {
                total += watcher.CurrentApprovals.Count;
            }
            return total;
        }
    }

    // Authors the tray-tooltip line for this plugin so it reads "pending
    // approvals" rather than the host's generic "pending". Null when nothing is
    // pending, so the host falls back to its own summary (or omits PIM entirely
    // when other providers have text).
    public string? BadgeTooltip
    {
        get
        {
            var count = Count;
            return count > 0
                ? $"Azure PIM — {count} pending approval{(count == 1 ? "" : "s")}"
                : null;
        }
    }

    // Pending approvals change fast; eligible roles change rarely. Match the
    // predecessor app's cadence.
    private static readonly TimeSpan PendingPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EligiblePollInterval = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, TenantWatchers> _watchersByTenant
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _watcherLock = new();
    private IPluginContext? _context;
    private CancellationTokenSource? _lifetimeCts;

    // Iteration helpers used by menu builders. Snapshots under the lock so the
    // menu thread never trips over a concurrent TenantReady / TenantRemoved.
    private PendingApprovalWatcher[] PendingWatchersSnapshot()
    {
        lock (_watcherLock) return _watchersByTenant.Values.Select(w => w.Pending).ToArray();
    }

    private EligibleRolesWatcher[] EligibleWatchersSnapshot()
    {
        lock (_watcherLock) return _watchersByTenant.Values.Select(w => w.Eligible).ToArray();
    }

    private sealed record TenantWatchers(PendingApprovalWatcher Pending, EligibleRolesWatcher Eligible);

    public string Id => "net.proxylayer.AzureTray.Plugin.PIM";

    public string DisplayName => "Azure PIM";

    public string Version { get; } = ResolveVersion();
    private static string ResolveVersion()
    {
        var asm = typeof(PimPlugin).Assembly;
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(v))
        {
            var plus = v.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? v[..plus] : v;
        }
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    public int ApiVersion => PluginApiVersion.Current;

    public IReadOnlyList<PluginPermissionRequirement> RequiredPermissions => PimRequiredPermissions.All;

    public IReadOnlyList<PluginMenuItem> GetMenuItems()
    {
        lock (_watcherLock)
        {
            if (_watchersByTenant.Count == 0)
            {
                return Array.Empty<PluginMenuItem>();
            }
        }

        return new[]
        {
            BuildPendingApprovalsMenu(),
            BuildOpenRequestMenu(),
        };
    }

    private PluginMenuItem BuildPendingApprovalsMenu()
    {
        var pendingWatchers = PendingWatchersSnapshot().ToArray();
        var totalPending = 0;
        foreach (var watcher in pendingWatchers)
        {
            totalPending += watcher.CurrentApprovals.Count;
        }

        var children = new List<PluginMenuItem>();
        var first = true;
        foreach (var watcher in pendingWatchers)
        {
            if (!first) children.Add(PluginMenuItem.Separator);
            first = false;

            var w = watcher;
            children.Add(new PluginMenuItem(
                // Text is the tenant name only — host renders the leading
                // refresh icon in its own animated slot via Icon = "↻".
                Text: w.TenantDisplayName,
                Invoke: () => _ = w.PollAsync(CancellationToken.None),
                IsBusy: w.IsPolling,
                KeepMenuOpen: true,
                Icon: "↻"));

            var pending = w.CurrentApprovals;
            if (pending.Count == 0)
            {
                children.Add(new PluginMenuItem("    (none)", IsEnabled: false));
            }
            else
            {
                foreach (var approval in pending)
                {
                    var a = approval;
                    children.Add(new PluginMenuItem(
                        Text: $"    {a.PrincipalDisplay} — {a.RoleDisplay}",
                        Invoke: () => _ = w.HandleNewApprovalAsync(a, CancellationToken.None)));
                }
            }
        }

        var label = totalPending > 0
            ? $"⏳  Pending Approvals  ({totalPending})"
            : "⏳  Pending Approvals";

        return new PluginMenuItem(Text: label, Children: children);
    }

    private PluginMenuItem BuildOpenRequestMenu()
    {
        var eligibleWatchers = EligibleWatchersSnapshot().ToArray();
        var totalEligible = 0;
        foreach (var watcher in eligibleWatchers)
        {
            totalEligible += watcher.CurrentEligibleRoles.Count;
        }

        var children = new List<PluginMenuItem>();
        var first = true;
        foreach (var watcher in eligibleWatchers)
        {
            if (!first) children.Add(PluginMenuItem.Separator);
            first = false;

            var w = watcher;
            children.Add(new PluginMenuItem(
                // Text is the tenant name only — host renders the leading
                // refresh icon in its own animated slot via Icon = "↻".
                Text: w.TenantDisplayName,
                Invoke: () => _ = w.PollAsync(CancellationToken.None),
                IsBusy: w.IsPolling,
                KeepMenuOpen: true,
                Icon: "↻"));

            var roles = w.CurrentEligibleRoles;
            if (roles.Count == 0)
            {
                children.Add(new PluginMenuItem("    (none)", IsEnabled: false));
                continue;
            }

            var entra = roles
                .Where(r => r.Source == PimSource.EntraId)
                .OrderBy(r => r.RoleName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var arm = roles
                .Where(r => r.Source == PimSource.AzureRbac)
                .OrderBy(r => r.RoleName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AppendSourceGroup(children, "Entra ID", entra, w);
            AppendSourceGroup(children, "Azure RBAC", arm, w);
        }

        var label = totalEligible > 0
            ? $"🔑  Open Request  ({totalEligible} role(s))"
            : "🔑  Open Request";

        return new PluginMenuItem(Text: label, Children: children);
    }

    private void AppendSourceGroup(
        List<PluginMenuItem> target,
        string sourceLabel,
        List<UnifiedEligibleRole> roles,
        EligibleRolesWatcher watcher)
    {
        if (roles.Count == 0) return;

        target.Add(new PluginMenuItem($"    — {sourceLabel} —", IsEnabled: false));

        var activeNames = watcher.CurrentActiveRoleNames;
        foreach (var role in roles)
        {
            var r = role;

            // Right-click "Copy role name" is offered on every row. Active rows
            // are otherwise non-actionable (the left-click is disabled), so the
            // context menu is the only place they can be acted on — context
            // items fire even though the row is greyed (IsEnabled: false).
            var copyName = new PluginMenuItem(
                Text: "Copy role name",
                Invoke: () => _context?.Clipboard.SetText(r.RoleName));

            if (activeNames.Contains(r.RoleName))
            {
                target.Add(new PluginMenuItem(
                    Text: $"    {r.RoleName}  ({r.ScopeDisplay})  ✓ active",
                    IsEnabled: false,
                    ContextItems: new[]
                    {
                        copyName,
                        new PluginMenuItem(
                            Text: "Deactivate",
                            Invoke: () => _ = watcher.HandleDeactivationAsync(r, CancellationToken.None)),
                    }));
            }
            else
            {
                target.Add(new PluginMenuItem(
                    Text: $"    {r.RoleName}  ({r.ScopeDisplay})",
                    Invoke: () => _ = watcher.HandleActivationAsync(r, CancellationToken.None),
                    ContextItems: new[] { copyName }));
            }
        }
    }

    // Tests exposed to the host's admin Test Runner.
    public IReadOnlyList<PluginTest> Tests => new[]
    {
        new PluginTest(
            "Force pending-approval poll",
            "Triggers PendingApprovalWatcher.PollAsync for every tracked tenant.",
            async ct =>
            {
                var watchers = PendingWatchersSnapshot();
                if (watchers.Length == 0) return PluginTestResult.Fail("No tenants tracked.");
                var total = 0;
                foreach (var w in watchers)
                {
                    await w.PollAsync(ct).ConfigureAwait(false);
                    total += w.CurrentApprovals.Count;
                }
                return PluginTestResult.Pass($"Polled {watchers.Length} tenant(s); {total} pending approval(s) total.");
            }),
        new PluginTest(
            "Force eligible-roles poll",
            "Triggers EligibleRolesWatcher.PollAsync for every tracked tenant.",
            async ct =>
            {
                var watchers = EligibleWatchersSnapshot();
                if (watchers.Length == 0) return PluginTestResult.Fail("No tenants tracked.");
                var total = 0;
                foreach (var w in watchers)
                {
                    await w.PollAsync(ct).ConfigureAwait(false);
                    total += w.CurrentEligibleRoles.Count;
                }
                return PluginTestResult.Pass($"Polled {watchers.Length} tenant(s); {total} eligible role(s) total.");
            }),
    };

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _lifetimeCts = new CancellationTokenSource();

        LogHostCompatibility(context);

        // Subscribe before backfill so we don't miss a tenant that becomes
        // ready in the same instant the plugin loads.
        context.TenantReady += OnTenantReady;
        context.TenantRemoved += OnTenantRemoved;

        foreach (var tenant in context.ReadyTenants)
        {
            StartWatchersFor(tenant);
        }

        context.Logger.LogInformation(
            "Azure PIM plugin initialized; {ReadyCount} tenant(s) already ready, approvals every {ApprovalIntervalSeconds}s, eligible roles every {EligibleIntervalSeconds}s.",
            context.ReadyTenants.Count,
            PendingPollInterval.TotalSeconds,
            EligiblePollInterval.TotalSeconds);

        return Task.CompletedTask;
    }

    // Host build this plugin was developed and validated against. The hard ABI
    // gate is ITrayPlugin.ApiVersion / PluginApiVersion; this is soft, forward-
    // looking capability detection via IPluginContext.HostVersion. When the host
    // gains a feature this plugin wants to use conditionally, bump this and gate
    // the feature on `host >= ValidatedAgainstHost` (or a feature-specific
    // minimum) right where you'd call it.
    private static readonly System.Version ValidatedAgainstHost = new(0, 6, 0);

    private static void LogHostCompatibility(IPluginContext context)
    {
        if (!System.Version.TryParse(context.HostVersion, out var host))
        {
            // Pre-capability hosts (and the contract's default) report no version.
            context.Logger.LogInformation(
                "Host did not report a version; assuming the baseline contract surface (plugin validated against host {Validated}).",
                ValidatedAgainstHost);
            return;
        }

        if (host < ValidatedAgainstHost)
        {
            context.Logger.LogWarning(
                "Running on host {Host}, older than the {Validated} this plugin build was validated against. Core features work; newer host capabilities are skipped.",
                host, ValidatedAgainstHost);
        }
        else
        {
            context.Logger.LogInformation(
                "Host {Host} meets the validated baseline {Validated}.", host, ValidatedAgainstHost);
        }
    }

    private void OnTenantReady(PluginTenant tenant) => StartWatchersFor(tenant);

    private void OnTenantRemoved(string tenantId) => _ = StopWatchersForAsync(tenantId);

    private void StartWatchersFor(PluginTenant tenant)
    {
        if (_lifetimeCts is null || _context is null) return;

        lock (_watcherLock)
        {
            if (_watchersByTenant.ContainsKey(tenant.TenantId)) return;

            // Each tenant gets its own client instances so token acquisition
            // is scoped to that specific tenant — no cross-tenant leakage.
            var graph = new GraphPimClient(_context, tenant.TenantId);
            var arm = new ArmPimClient(_context, tenant.TenantId);

            // Eligibility runs first so its subscription set is available to
            // the pending watcher's relevant-subs filter (captured by Func).
            var eligible = new EligibleRolesWatcher(graph, arm, _context, tenant, EligiblePollInterval);
            eligible.PollStarted += OnWatcherPollStarted;
            eligible.PollCompleted += OnWatcherPollCompleted;
            eligible.Start(_lifetimeCts.Token);

            var pending = new PendingApprovalWatcher(
                graph, arm, _context, tenant, PendingPollInterval,
                relevantSubscriptions: () => eligible.RelevantSubscriptionIds);
            pending.PollStarted += OnWatcherPollStarted;
            pending.PollCompleted += OnWatcherPollCompleted;
            pending.Start(_lifetimeCts.Token);

            _watchersByTenant[tenant.TenantId] = new TenantWatchers(pending, eligible);
            _context.Logger.LogInformation(
                "Started PIM watchers for tenant {TenantId} ({DisplayName}).",
                tenant.TenantId, tenant.DisplayName);
        }
        MenuChanged?.Invoke();
    }

    private async Task StopWatchersForAsync(string tenantId)
    {
        TenantWatchers? entry;
        lock (_watcherLock)
        {
            if (!_watchersByTenant.TryGetValue(tenantId, out entry)) return;
            _watchersByTenant.Remove(tenantId);
        }

        entry.Pending.PollStarted -= OnWatcherPollStarted;
        entry.Pending.PollCompleted -= OnWatcherPollCompleted;
        entry.Eligible.PollStarted -= OnWatcherPollStarted;
        entry.Eligible.PollCompleted -= OnWatcherPollCompleted;

        await entry.Pending.StopAsync().ConfigureAwait(false);
        await entry.Eligible.StopAsync().ConfigureAwait(false);

        _context?.Logger.LogInformation("Stopped PIM watchers for tenant {TenantId}.", tenantId);
        MenuChanged?.Invoke();
    }

    // Plugin fires MenuChanged on the state transitions (busy → idle, idle →
    // busy). The host's spinner timer handles per-frame animation in place
    // so the rest of the menu stays still.
    private void OnWatcherPollStarted() => MenuChanged?.Invoke();
    private void OnWatcherPollCompleted()
    {
        MenuChanged?.Invoke();
        // Pending count may have shifted; let the host re-render the tray icon.
        BadgeChanged?.Invoke();
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_context is not null)
        {
            _context.TenantReady -= OnTenantReady;
            _context.TenantRemoved -= OnTenantRemoved;
        }

        _lifetimeCts?.Cancel();

        TenantWatchers[] toStop;
        lock (_watcherLock)
        {
            toStop = _watchersByTenant.Values.ToArray();
            _watchersByTenant.Clear();
        }

        foreach (var entry in toStop)
        {
            entry.Pending.PollStarted -= OnWatcherPollStarted;
            entry.Pending.PollCompleted -= OnWatcherPollCompleted;
            entry.Eligible.PollStarted -= OnWatcherPollStarted;
            entry.Eligible.PollCompleted -= OnWatcherPollCompleted;
            await entry.Pending.StopAsync().ConfigureAwait(false);
            await entry.Eligible.StopAsync().ConfigureAwait(false);
        }

        _context = null;
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
    }

    public void Dispose()
    {
        // The host always calls ShutdownAsync first; Dispose is a defensive
        // fallback for callers that activate the plugin outside the host lifecycle.
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
    }
}
