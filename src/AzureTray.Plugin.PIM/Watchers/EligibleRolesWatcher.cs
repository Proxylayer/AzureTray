using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Graph;

namespace AzureTray.Plugin.PIM.Watchers;

// One watcher per tenant. Polls eligible roles from both Graph (Entra ID) and
// ARM (Azure RBAC) on a slow cadence (30 minutes by default — eligibility
// changes infrequently). The user can force an immediate refresh from the
// tray menu's "↻ <Tenant>" entry. Activation is initiated by clicking a role:
// duration prompt → justification prompt → call the matching API.
internal sealed class EligibleRolesWatcher
{
    private static readonly string[] DurationChoices = { "1 hour", "4 hours", "8 hours" };

    private readonly IGraphPimClient _graph;
    private readonly IArmPimClient _arm;
    private readonly IPluginContext _context;
    private readonly PluginTenant _tenant;
    private readonly TimeSpan _interval;

    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private UnifiedEligibleRole[] _lastSnapshot = Array.Empty<UnifiedEligibleRole>();
    private IReadOnlySet<string> _activeRoleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _relevantSubscriptionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string? _cachedPrincipalId;

    public EligibleRolesWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        IPluginContext context,
        PluginTenant tenant,
        TimeSpan interval)
    {
        _graph = graph;
        _arm = arm;
        _context = context;
        _tenant = tenant;
        _interval = interval;
    }

    // Raised at the start and end of each PollAsync so the host can spin a
    // refresh indicator and rebuild the menu. May fire on a thread-pool thread;
    // subscribers must marshal as needed.
    public event Action? PollStarted;
    public event Action? PollCompleted;

    public string TenantId => _tenant.TenantId;
    public string TenantDisplayName => _tenant.DisplayName;
    public IReadOnlyList<UnifiedEligibleRole> CurrentEligibleRoles => _lastSnapshot;

    public bool IsPolling { get; private set; }

    // Display names of role assignments currently active for the signed-in user
    // in this tenant. The menu uses this set to gray out eligible roles already
    // activated (predecessor behavior). Sourced from Graph only — ARM eligible
    // roles share the same name set, matching the original implementation.
    public IReadOnlySet<string> CurrentActiveRoleNames => _activeRoleNames;

    // Subscription IDs where the signed-in user has at least one ARM eligible
    // role. PendingApprovalWatcher reads this to skip subscriptions where the
    // user has no role to activate — cuts ARM fan-out from "every sub in the
    // tenant" (often 30+) to "subs the user can act on" (typically 3-5).
    // Empty until the first successful poll; pending watcher falls back to
    // scanning all subs when empty.
    public IReadOnlySet<string> RelevantSubscriptionIds => _relevantSubscriptionIds;

    public void Start(CancellationToken stopToken)
    {
        // Hydrate from cache first so the menu shows last-known eligibility
        // immediately instead of waiting for the first poll to finish. The
        // background loop refreshes shortly after and overwrites the cache.
        LoadFromCache();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
    }

    private string CachePath =>
        Path.Combine(_context.DataDir, $"eligible-roles-{Sanitize(_tenant.TenantId)}.json");

    private static string Sanitize(string s)
        => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));

    private void LoadFromCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            using var stream = File.OpenRead(CachePath);
            var dto = JsonSerializer.Deserialize<CacheDto>(stream);
            if (dto is null) return;

            _lastSnapshot = dto.Roles ?? Array.Empty<UnifiedEligibleRole>();
            _activeRoleNames = dto.ActiveRoleNames is { Count: > 0 }
                ? new HashSet<string>(dto.ActiveRoleNames, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _relevantSubscriptionIds = dto.RelevantSubscriptionIds is { Count: > 0 }
                ? new HashSet<string>(dto.RelevantSubscriptionIds, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _context.Logger.LogInformation(
                "Eligible-role cache loaded for tenant {TenantId}: {Count} role(s).",
                _tenant.TenantId, _lastSnapshot.Length);
        }
        catch (Exception ex)
        {
            // Stale or hand-edited cache shouldn't block startup. Drop it
            // and let the first poll repopulate.
            _context.Logger.LogWarning(ex,
                "Eligible-role cache load failed for tenant {TenantId}; ignoring.",
                _tenant.TenantId);
        }
    }

    private void SaveToCache()
    {
        try
        {
            Directory.CreateDirectory(_context.DataDir);
            var dto = new CacheDto
            {
                Roles = _lastSnapshot.ToArray(),
                ActiveRoleNames = _activeRoleNames.ToList(),
                RelevantSubscriptionIds = _relevantSubscriptionIds.ToList(),
            };
            using var stream = File.Create(CachePath);
            JsonSerializer.Serialize(stream, dto);
        }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(ex,
                "Eligible-role cache save failed for tenant {TenantId}.",
                _tenant.TenantId);
        }
    }

    private sealed class CacheDto
    {
        public UnifiedEligibleRole[]? Roles { get; set; }
        public List<string>? ActiveRoleNames { get; set; }
        public List<string>? RelevantSubscriptionIds { get; set; }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _context.Logger.LogWarning(
                    ex,
                    "Eligible-role poll failed for tenant {TenantId}; will retry next interval.",
                    _tenant.TenantId);
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        IsPolling = true;
        PollStarted?.Invoke();
        try
        {
            var principalId = await GetPrincipalIdAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(principalId))
            {
                _lastSnapshot = Array.Empty<UnifiedEligibleRole>();
                _activeRoleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var graphTask = FetchGraphAsync(principalId, cancellationToken);
            var armTask = FetchArmAsync(principalId, cancellationToken);
            var activeTask = FetchActiveRoleNamesAsync(principalId, cancellationToken);

            var graphRoles = await graphTask.ConfigureAwait(false);
            var armRoles = await armTask.ConfigureAwait(false);
            var activeNames = await activeTask.ConfigureAwait(false);

            _lastSnapshot = graphRoles.Concat(armRoles).ToArray();
            _activeRoleNames = activeNames;
            _relevantSubscriptionIds = ExtractSubscriptionIds(armRoles);
            SaveToCache();
        }
        finally
        {
            IsPolling = false;
            PollCompleted?.Invoke();
        }
    }

    private static HashSet<string> ExtractSubscriptionIds(IEnumerable<UnifiedEligibleRole> armRoles)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in armRoles)
        {
            var subId = ExtractSubscriptionId(role.ArmScope);
            if (!string.IsNullOrEmpty(subId)) set.Add(subId);
        }
        return set;
    }

    // Pulls the subscription GUID out of an ARM scope. Accepts:
    //   /subscriptions/{id}
    //   /subscriptions/{id}/resourceGroups/{rg}
    //   /subscriptions/{id}/resourceGroups/{rg}/providers/...
    internal static string? ExtractSubscriptionId(string? armScope)
    {
        if (string.IsNullOrWhiteSpace(armScope)) return null;
        var trimmed = armScope.TrimStart('/');
        const string prefix = "subscriptions/";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var remainder = trimmed.AsSpan(prefix.Length);
        var slash = remainder.IndexOf('/');
        return slash < 0 ? remainder.ToString() : remainder[..slash].ToString();
    }

    private async Task<IReadOnlySet<string>> FetchActiveRoleNamesAsync(string principalId, CancellationToken ct)
    {
        try
        {
            var actives = await _graph.ListActiveRoleAssignmentsAsync(_tenant.TenantId, principalId, ct).ConfigureAwait(false);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in actives)
            {
                var name = item.RoleDefinition?.DisplayName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    set.Add(name!);
                }
            }
            return set;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "Active-role fetch failed for tenant {TenantId}; eligibility list will not gray out active roles this cycle.",
                _tenant.TenantId);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal async Task HandleActivationAsync(UnifiedEligibleRole role, CancellationToken cancellationToken)
    {
        try
        {
            var principalId = _cachedPrincipalId
                ?? await GetPrincipalIdAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(principalId))
            {
                _context.Logger.LogWarning(
                    "Cannot activate {RoleName} on tenant {TenantId}: signed-in principal ID could not be resolved.",
                    role.RoleName, _tenant.TenantId);
                return;
            }

            var durationChoice = await _context.Notifier.ShowAsync(
                new ChoiceRequest(
                    Title: $"Activate {role.RoleName}",
                    Message: $"on {role.ScopeDisplay}. How long?",
                    Choices: DurationChoices),
                cancellationToken).ConfigureAwait(false);

            if (durationChoice is not ChoiceResult { SelectedChoice: { } pickedLabel }
                || !TryParseDuration(pickedLabel, out var duration))
            {
                _context.Logger.LogDebug(
                    "Activation cancelled at duration prompt for {RoleName} on tenant {TenantId}.",
                    role.RoleName, _tenant.TenantId);
                return;
            }

            var justification = await _context.Notifier.ShowAsync(
                new TextInputRequest(
                    Title: $"Justification — {role.RoleName}",
                    Message: $"Why are you activating {role.RoleName}?",
                    Placeholder: "Required"),
                cancellationToken).ConfigureAwait(false);

            if (justification is not TextInputResult { Text: { } justText } || string.IsNullOrWhiteSpace(justText))
            {
                _context.Logger.LogInformation(
                    "Activation cancelled at justification prompt for {RoleName} on tenant {TenantId}.",
                    role.RoleName, _tenant.TenantId);
                return;
            }

            switch (role.Source)
            {
                case PimSource.EntraId:
                    await _graph.ActivateRoleAsync(
                        _tenant.TenantId,
                        principalId,
                        role.RoleDefinitionId,
                        duration,
                        justText,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case PimSource.AzureRbac:
                    if (string.IsNullOrWhiteSpace(role.ArmScope))
                    {
                        _context.Logger.LogError(
                            "ARM role {RoleName} on tenant {TenantId} has no scope; cannot activate.",
                            role.RoleName, _tenant.TenantId);
                        await NotifyActivationErrorAsync(role, $"Cannot activate — the role has no ARM scope to act on.", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(role.EligibilityId))
                    {
                        _context.Logger.LogError(
                            "ARM role {RoleName} on tenant {TenantId} has no eligibility id; cannot activate.",
                            role.RoleName, _tenant.TenantId);
                        await NotifyActivationErrorAsync(role, $"Cannot activate — the role has no eligibility id.", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await _arm.ActivateRoleAsync(
                        _tenant.TenantId,
                        role.ArmScope,
                        principalId,
                        role.RoleDefinitionId,
                        role.EligibilityId,
                        duration,
                        justText,
                        cancellationToken).ConfigureAwait(false);
                    break;
            }

            // Surface the success to the user so they know the request
            // landed. Notification auto-dismisses (InformationRequest).
            _ = _context.Notifier.ShowAsync(
                new InformationRequest(
                    Title: $"Activated {role.RoleName}",
                    Message: $"on {role.ScopeDisplay} for {FormatDuration(duration)}.")
                {
                    Severity = NotificationSeverity.Info,
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _context.Logger.LogError(
                ex,
                "Activation failed for {RoleName} on tenant {TenantId}.",
                role.RoleName, _tenant.TenantId);
            await NotifyActivationErrorAsync(role, ExtractReason(ex), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NotifyActivationErrorAsync(UnifiedEligibleRole role, string reason, CancellationToken cancellationToken)
    {
        // Error-severity InformationRequest = red accent stripe, auto-dismiss
        // after a few seconds. Most "what went wrong" the user needs to know
        // is the Graph/ARM error message, which now flows through the
        // exception thanks to EnsureSuccessOrThrowWithBodyAsync.
        await _context.Notifier.ShowAsync(
            new InformationRequest(
                Title: $"Activation failed: {role.RoleName}",
                Message: reason)
            {
                Severity = NotificationSeverity.Error,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string ExtractReason(Exception ex)
    {
        // Strip our wrapper prefix so the user sees the underlying service
        // error first. Body comes from EnsureSuccessOrThrowWithBodyAsync.
        var message = ex.Message;
        var bodyIdx = message.IndexOf("Body: ", StringComparison.Ordinal);
        if (bodyIdx > 0)
        {
            var prefix = message[..bodyIdx].TrimEnd('.', ' ');
            var body = message[(bodyIdx + "Body: ".Length)..];
            return $"{prefix}\n\n{body}";
        }
        return message;
    }

    private static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min";
        if (d.TotalHours < 24) return d.Minutes == 0 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{(int)d.TotalDays}d";
    }

    private async Task<string?> GetPrincipalIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cachedPrincipalId)) return _cachedPrincipalId;
        try
        {
            _cachedPrincipalId = await _graph.GetSignedInUserIdAsync(_tenant.TenantId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "Could not resolve signed-in user for tenant {TenantId}; eligible-role poll will retry.",
                _tenant.TenantId);
        }
        return _cachedPrincipalId;
    }

    private async Task<List<UnifiedEligibleRole>> FetchGraphAsync(string principalId, CancellationToken ct)
    {
        try
        {
            var schedules = await _graph.ListEligibleRolesAsync(_tenant.TenantId, principalId, ct).ConfigureAwait(false);
            return schedules
                .Where(s => !string.IsNullOrWhiteSpace(s.RoleDefinitionId))
                .Select(s => new UnifiedEligibleRole(
                    Source: PimSource.EntraId,
                    RoleName: s.RoleDefinition?.DisplayName ?? "(unknown role)",
                    RoleDefinitionId: s.RoleDefinitionId!,
                    ScopeDisplay: "Entra ID directory",
                    ArmScope: null,
                    EligibilityId: s.Id))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "Graph eligible-role fetch failed for tenant {TenantId}; continuing with ARM only.",
                _tenant.TenantId);
            return new();
        }
    }

    private async Task<List<UnifiedEligibleRole>> FetchArmAsync(string principalId, CancellationToken ct)
    {
        try
        {
            var subs = await _arm.ListSubscriptionsAsync(_tenant.TenantId, ct).ConfigureAwait(false);
            if (subs.Count == 0) return new();

            var scopes = subs
                .Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId))
                .Select(s => $"/subscriptions/{s.SubscriptionId}")
                .ToList();
            if (scopes.Count == 0) return new();

            var schedules = await _arm.ListEligibleRolesAsync(_tenant.TenantId, principalId, scopes, ct).ConfigureAwait(false);

            return schedules
                .Where(s => !string.IsNullOrWhiteSpace(s.Properties?.RoleDefinitionId))
                .Select(s => new UnifiedEligibleRole(
                    Source: PimSource.AzureRbac,
                    RoleName: s.Properties!.ExpandedProperties?.RoleDefinition?.DisplayName ?? "(unknown role)",
                    RoleDefinitionId: s.Properties.RoleDefinitionId!,
                    ScopeDisplay: s.Properties.ExpandedProperties?.Scope?.DisplayName ?? s.Properties.Scope ?? "(unknown scope)",
                    ArmScope: s.Properties.Scope,
                    EligibilityId: s.Id))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "ARM eligible-role fetch failed for tenant {TenantId}; continuing with Graph only.",
                _tenant.TenantId);
            return new();
        }
    }

    private static bool TryParseDuration(string label, out TimeSpan duration)
    {
        duration = label switch
        {
            "1 hour" => TimeSpan.FromHours(1),
            "4 hours" => TimeSpan.FromHours(4),
            "8 hours" => TimeSpan.FromHours(8),
            _ => TimeSpan.Zero,
        };
        return duration > TimeSpan.Zero;
    }
}
