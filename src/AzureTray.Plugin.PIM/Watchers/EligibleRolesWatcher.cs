using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Groups;
using AzureTray.Plugin.PIM.Policies;

namespace AzureTray.Plugin.PIM.Watchers;

// One watcher per tenant. Polls eligible roles from Graph (Entra ID directory
// roles), ARM (Azure RBAC) and Graph again (PIM for Groups membership and
// ownership) on a slow cadence (30 minutes by default — eligibility changes
// infrequently). The user can force an immediate refresh from the tray menu's
// "↻ <Tenant>" entry. Activation is initiated by clicking a role: duration
// prompt → justification prompt → call the matching API.
//
// The three sources are fetched concurrently and each is wrapped in its own
// try/catch: one provider failing degrades that provider's rows to empty and
// must never blank the other two.
internal sealed class EligibleRolesWatcher
{
    private readonly IGraphPimClient _graph;
    private readonly IArmPimClient _arm;
    private readonly IGraphGroupPimClient _groups;
    private readonly IPluginContext _context;
    private readonly PluginTenant _tenant;
    private readonly TimeSpan _interval;
    private readonly PendingActivationStore _pendingActivations;

    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private UnifiedEligibleRole[] _lastSnapshot = Array.Empty<UnifiedEligibleRole>();
    private ActiveRoleAssignment[] _activeAssignments = Array.Empty<ActiveRoleAssignment>();
    private IReadOnlySet<string> _relevantSubscriptionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _relevantManagementGroupScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string? _cachedPrincipalId;

    public EligibleRolesWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        IGraphGroupPimClient groups,
        IPluginContext context,
        PluginTenant tenant,
        TimeSpan interval,
        PendingActivationStore pendingActivations)
    {
        _graph = graph;
        _arm = arm;
        _groups = groups;
        _context = context;
        _tenant = tenant;
        _interval = interval;
        _pendingActivations = pendingActivations;
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

    // Role assignments currently in force for the signed-in user in this tenant,
    // fetched per provider (Graph for Entra ID directory roles and for PIM group
    // access, ARM for Azure RBAC). The menu
    // uses these to gray out eligible roles that are already activated and to
    // show how long each activation has left.
    public IReadOnlyList<ActiveRoleAssignment> CurrentActiveAssignments => _activeAssignments;

    // The assignment backing an eligible-role row, or null when the row is not
    // currently active. Matched within the row's own provider — see
    // ActiveRoleAssignment.Matches.
    public ActiveRoleAssignment? FindActiveFor(UnifiedEligibleRole role)
    {
        foreach (var assignment in _activeAssignments)
        {
            if (assignment.Matches(role)) return assignment;
        }
        return null;
    }

    // Subscription IDs where the signed-in user has at least one ARM eligible
    // role. PendingApprovalWatcher reads this to skip subscriptions where the
    // user has no role to activate — cuts ARM fan-out from "every sub in the
    // tenant" (often 30+) to "subs the user can act on" (typically 3-5).
    // Empty until the first successful poll; pending watcher falls back to
    // scanning all subs when empty.
    public IReadOnlySet<string> RelevantSubscriptionIds => _relevantSubscriptionIds;

    // Full ARM scope strings (/providers/Microsoft.Management/managementGroups/{id})
    // where the signed-in user holds an eligible role. Management-group-scoped
    // eligibility surfaces through the per-subscription fan-out as inherited
    // entries whose scope is the MG itself — ExtractSubscriptionId returns null
    // for those, so without this set the pending-approval watcher would never
    // query the MG scope and MG-scoped activation requests from other users
    // would be invisible to the approver feed. Empty when the user has no
    // MG-scoped eligibility; the pending watcher then adds no extra requests.
    public IReadOnlySet<string> RelevantManagementGroupScopes => _relevantManagementGroupScopes;

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

            // Deduplicated on the way in as well as on the way out: a cache file
            // written before the collapse existed would otherwise show its
            // duplicate rows until the first poll lands, half an hour later.
            _lastSnapshot = dto.Roles is { Length: > 0 }
                ? EligibleRoleDeduplicator.Deduplicate(dto.Roles).ToArray()
                : Array.Empty<UnifiedEligibleRole>();
            // Caches written before actives carried end times simply have no
            // ActiveAssignments member; unknown/missing members are ignored, so
            // a legacy file loads as "eligibility known, actives unknown" and
            // the first poll fills them in.
            _activeAssignments = dto.ActiveAssignments ?? Array.Empty<ActiveRoleAssignment>();
            _relevantSubscriptionIds = dto.RelevantSubscriptionIds is { Count: > 0 }
                ? new HashSet<string>(dto.RelevantSubscriptionIds, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Caches written before MG scopes existed have no member here; that
            // loads as "no known MG scopes" and the first poll fills it in.
            _relevantManagementGroupScopes = dto.RelevantManagementGroupScopes is { Count: > 0 }
                ? new HashSet<string>(dto.RelevantManagementGroupScopes, StringComparer.OrdinalIgnoreCase)
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
                ActiveAssignments = _activeAssignments.ToArray(),
                RelevantSubscriptionIds = _relevantSubscriptionIds.ToList(),
                RelevantManagementGroupScopes = _relevantManagementGroupScopes.ToList(),
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
        public ActiveRoleAssignment[]? ActiveAssignments { get; set; }
        public List<string>? RelevantSubscriptionIds { get; set; }
        public List<string>? RelevantManagementGroupScopes { get; set; }
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
                _activeAssignments = Array.Empty<ActiveRoleAssignment>();
                return;
            }

            var graphTask = FetchGraphAsync(principalId, cancellationToken);
            var armTask = FetchArmAsync(principalId, cancellationToken);
            var groupTask = FetchGroupsAsync(cancellationToken);
            var graphActiveTask = FetchGraphActiveAssignmentsAsync(principalId, cancellationToken);

            var graphRoles = await graphTask.ConfigureAwait(false);
            var arm = await armTask.ConfigureAwait(false);
            var groups = await groupTask.ConfigureAwait(false);
            var graphActives = await graphActiveTask.ConfigureAwait(false);

            _lastSnapshot = graphRoles.Concat(arm.Roles).Concat(groups.Roles).ToArray();
            _activeAssignments = graphActives
                .Concat(arm.ActiveAssignments)
                .Concat(groups.ActiveAssignments)
                .ToArray();
            _relevantSubscriptionIds = ExtractSubscriptionIds(arm.Roles);
            _relevantManagementGroupScopes = ExtractManagementGroupScopes(arm.Roles);
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

    private static HashSet<string> ExtractManagementGroupScopes(IEnumerable<UnifiedEligibleRole> armRoles)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in armRoles)
        {
            var mgScope = ExtractManagementGroupScope(role.ArmScope);
            if (!string.IsNullOrEmpty(mgScope)) set.Add(mgScope);
        }
        return set;
    }

    // Pulls the management-group scope prefix out of an ARM scope, normalized
    // with a leading slash. Accepts:
    //   /providers/Microsoft.Management/managementGroups/{id}
    // (and tolerates trailing segments, though MG scopes have none in practice).
    // Returns null for subscription and other non-MG scopes.
    internal static string? ExtractManagementGroupScope(string? armScope)
    {
        if (string.IsNullOrWhiteSpace(armScope)) return null;
        var trimmed = armScope.TrimStart('/');
        const string prefix = "providers/Microsoft.Management/managementGroups/";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var remainder = trimmed.AsSpan(prefix.Length);
        if (remainder.IsEmpty) return null;
        var slash = remainder.IndexOf('/');
        var mgId = slash < 0 ? remainder.ToString() : remainder[..slash].ToString();
        return string.IsNullOrWhiteSpace(mgId) ? null : $"/{prefix}{mgId}";
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

    private async Task<List<ActiveRoleAssignment>> FetchGraphActiveAssignmentsAsync(string principalId, CancellationToken ct)
    {
        try
        {
            var actives = await _graph.ListActiveRoleAssignmentsAsync(principalId, ct).ConfigureAwait(false);
            return actives
                .Select(a => new ActiveRoleAssignment(
                    Source: PimSource.EntraId,
                    RoleName: a.RoleDefinition?.DisplayName ?? "(unknown role)",
                    RoleDefinitionId: a.RoleDefinitionId,
                    Scope: a.DirectoryScopeId,
                    EndDateTime: a.EndDateTime))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "Entra active-role fetch failed for tenant {TenantId}; eligibility list will not gray out active roles this cycle.",
                _tenant.TenantId);
            return new();
        }
    }

    private async Task<List<ActiveRoleAssignment>> FetchArmActiveAssignmentsAsync(
        string principalId, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        try
        {
            var actives = await _arm.ListActiveRoleAssignmentsAsync(principalId, scopes, ct).ConfigureAwait(false);
            return actives
                .Where(a => a.Properties is not null)
                .Select(a => new ActiveRoleAssignment(
                    Source: PimSource.AzureRbac,
                    RoleName: a.Properties!.ExpandedProperties?.RoleDefinition?.DisplayName ?? "(unknown role)",
                    RoleDefinitionId: a.Properties.RoleDefinitionId,
                    Scope: a.Properties.Scope,
                    EndDateTime: a.Properties.EndDateTime))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            // Caught separately from the eligibility fan-out so a failure here
            // still leaves the ARM eligible roles listed (just not grayed out).
            _context.Logger.LogWarning(
                ex,
                "ARM active-role fetch failed for tenant {TenantId}; Azure RBAC rows will not gray out this cycle.",
                _tenant.TenantId);
            return new();
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

            // Clamped to the role's policy maximum: offering a longer duration
            // than the policy permits earns a 400 from the service that reads
            // as a generic activation failure.
            var choices = ActivationDurationChoices.For(role);
            var durationChoice = await _context.Notifier.ShowAsync(
                new ChoiceRequest(
                    Title: $"Activate {role.RoleName}",
                    Message: $"Activate on {role.ScopeDisplay}. Choose a duration.",
                    Choices: choices.Select(c => c.Label).ToArray()),
                cancellationToken).ConfigureAwait(false);

            if (durationChoice is not ChoiceResult { SelectedChoice: { } pickedLabel }
                || ActivationDurationChoices.Match(choices, pickedLabel) is not { } duration)
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
                    Placeholder: "Reason for activating (required)"),
                cancellationToken).ConfigureAwait(false);

            if (justification is not TextInputResult { Text: { } justText } || string.IsNullOrWhiteSpace(justText))
            {
                _context.Logger.LogInformation(
                    "Activation cancelled at justification prompt for {RoleName} on tenant {TenantId}.",
                    role.RoleName, _tenant.TenantId);
                return;
            }

            string? status = null;
            switch (role.Source)
            {
                case PimSource.EntraId:
                {
                    var created = await _graph.ActivateRoleAsync(
                        principalId,
                        role.RoleDefinitionId,
                        EntraDirectoryScope.OrDirectory(role.DirectoryScopeId),
                        duration,
                        justText,
                        cancellationToken).ConfigureAwait(false);
                    status = created.Status;
                    TrackIfAwaitingApproval(role, created.Id, status);
                    break;
                }

                case PimSource.AzureRbac:
                    if (string.IsNullOrWhiteSpace(role.ArmScope))
                    {
                        _context.Logger.LogError(
                            "ARM role {RoleName} on tenant {TenantId} has no scope; cannot activate.",
                            role.RoleName, _tenant.TenantId);
                        await NotifyActivationErrorAsync(role, $"Cannot activate — the role has no ARM scope to act on.", ex: null, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(role.EligibilityId))
                    {
                        // linkedRoleEligibilityScheduleId is optional on ARM's
                        // roleAssignmentScheduleRequests contract, so a missing
                        // one is not a reason to refuse: warn and let ARM decide
                        // rather than leaving the row dead in the menu.
                        _context.Logger.LogWarning(
                            "ARM role {RoleName} on tenant {TenantId} has no eligibility id; activating without linkedRoleEligibilityScheduleId.",
                            role.RoleName, _tenant.TenantId);
                    }
                    var armRequest = await _arm.ActivateRoleAsync(
                        role.ArmScope,
                        principalId,
                        role.RoleDefinitionId,
                        role.EligibilityId,
                        duration,
                        justText,
                        cancellationToken).ConfigureAwait(false);
                    status = armRequest.Properties?.Status;
                    // ARM's request id is the PUT's resource name; GetActivationStatusAsync
                    // rebuilds the URL from it, so pass the name rather than the full id.
                    TrackIfAwaitingApproval(role, armRequest.Name ?? LastSegment(armRequest.Id), status);
                    break;

                case PimSource.EntraGroup:
                {
                    if (string.IsNullOrWhiteSpace(role.GroupId))
                    {
                        // The group id is the row's scope; without it there is
                        // nothing to activate against. Only reachable from a
                        // hand-edited cache file.
                        _context.Logger.LogError(
                            "Group access {RoleName} on tenant {TenantId} has no group id; cannot activate.",
                            role.RoleName, _tenant.TenantId);
                        await NotifyActivationErrorAsync(role, "Cannot activate — the row has no group to act on.", ex: null, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    var groupRequest = await _groups.ActivateAsync(
                        principalId,
                        role.GroupId!,
                        role.RoleDefinitionId,
                        duration,
                        justText,
                        cancellationToken).ConfigureAwait(false);
                    status = groupRequest.Status;
                    TrackIfAwaitingApproval(role, groupRequest.Id, status);
                    break;
                }
            }

            // Surface the outcome so the user knows the request landed, and
            // whether it granted access outright or went to an approver.
            // Notification auto-dismisses (InformationRequest).
            var awaitingApproval = !ActivationStatus.IsProvisioned(status);
            _ = _context.Notifier.ShowAsync(
                new InformationRequest(
                    Title: awaitingApproval ? $"Requested {role.RoleName}" : $"Activated {role.RoleName}",
                    Message: awaitingApproval
                        ? $"on {role.ScopeDisplay} for {FormatDuration(duration)} — awaiting approval."
                        : $"on {role.ScopeDisplay} for {FormatDuration(duration)}.")
                {
                    Severity = NotificationSeverity.Success,
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
            await NotifyActivationErrorAsync(role, ExtractHeadline(ex), ex, cancellationToken).ConfigureAwait(false);
        }
    }

    // An activation that comes back already Provisioned granted access outright
    // (no approval policy) and needs no follow-up. Anything else is sitting with
    // an approver: record it so PendingActivationWatcher can notice the approval
    // and get the new role claims into the access token.
    private void TrackIfAwaitingApproval(UnifiedEligibleRole role, string? requestId, string? status)
    {
        if (ActivationStatus.IsProvisioned(status)) return;

        if (string.IsNullOrWhiteSpace(requestId))
        {
            _context.Logger.LogWarning(
                "Activation of {RoleName} on tenant {TenantId} returned status {Status} but no request id; cannot track the approval.",
                role.RoleName, _tenant.TenantId, status);
            return;
        }

        if (ActivationStatus.IsTerminalFailure(status))
        {
            _context.Logger.LogInformation(
                "Activation of {RoleName} on tenant {TenantId} came back {Status}; not tracking.",
                role.RoleName, _tenant.TenantId, status);
            return;
        }

        _pendingActivations.Track(new PendingActivationRequest(
            Source: role.Source,
            RequestId: requestId!,
            RoleName: role.RoleName,
            ScopeDisplay: role.ScopeDisplay,
            ArmScope: role.ArmScope,
            SubmittedAt: DateTimeOffset.UtcNow));
    }

    private static string? LastSegment(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        var last = resourceId.LastIndexOf('/');
        return last >= 0 && last < resourceId.Length - 1 ? resourceId[(last + 1)..] : resourceId;
    }

    internal async Task HandleDeactivationAsync(UnifiedEligibleRole role, CancellationToken cancellationToken)
    {
        try
        {
            var confirm = await _context.Notifier.ShowAsync(
                new YesNoRequest(
                    Title: $"Deactivate {role.RoleName}?",
                    Message: $"End your active assignment on {role.ScopeDisplay} now."),
                cancellationToken).ConfigureAwait(false);

            if (confirm is not YesNoResult { Accepted: true })
            {
                _context.Logger.LogDebug(
                    "Deactivation cancelled at confirm prompt for {RoleName} on tenant {TenantId}.",
                    role.RoleName, _tenant.TenantId);
                return;
            }

            var principalId = _cachedPrincipalId
                ?? await GetPrincipalIdAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(principalId))
            {
                _context.Logger.LogWarning(
                    "Cannot deactivate {RoleName} on tenant {TenantId}: signed-in principal ID could not be resolved.",
                    role.RoleName, _tenant.TenantId);
                return;
            }

            const string justification = "Deactivated from AzureTray.";

            switch (role.Source)
            {
                case PimSource.EntraId:
                    await _graph.DeactivateRoleAsync(
                        principalId,
                        role.RoleDefinitionId,
                        EntraDirectoryScope.OrDirectory(role.DirectoryScopeId),
                        justification,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case PimSource.AzureRbac:
                    if (string.IsNullOrWhiteSpace(role.ArmScope))
                    {
                        _context.Logger.LogError(
                            "ARM role {RoleName} on tenant {TenantId} has no scope; cannot deactivate.",
                            role.RoleName, _tenant.TenantId);
                        await NotifyOperationErrorAsync("Deactivation", role, "Cannot deactivate — the role has no ARM scope to act on.", ex: null, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    await _arm.DeactivateRoleAsync(
                        role.ArmScope,
                        principalId,
                        role.RoleDefinitionId,
                        justification,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case PimSource.EntraGroup:
                    if (string.IsNullOrWhiteSpace(role.GroupId))
                    {
                        _context.Logger.LogError(
                            "Group access {RoleName} on tenant {TenantId} has no group id; cannot deactivate.",
                            role.RoleName, _tenant.TenantId);
                        await NotifyOperationErrorAsync("Deactivation", role, "Cannot deactivate — the row has no group to act on.", ex: null, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    // A 2xx is not a guarantee the access is gone: when dropping
                    // the last active owner would leave the group ownerless, PIM
                    // accepts the request and then silently retries the removal
                    // for up to 30 days. The refresh below is what tells the
                    // truth — the row stays marked active if it did not take.
                    await _groups.DeactivateAsync(
                        principalId,
                        role.GroupId!,
                        role.RoleDefinitionId,
                        justification,
                        cancellationToken).ConfigureAwait(false);
                    break;
            }

            _ = _context.Notifier.ShowAsync(
                new InformationRequest(
                    Title: $"Deactivated {role.RoleName}",
                    Message: $"on {role.ScopeDisplay}.")
                {
                    Severity = NotificationSeverity.Success,
                },
                CancellationToken.None);

            // Refresh so the menu's "✓ active" grey-out clears. The directory
            // can lag a beat behind the request landing, so a manual "↻" is the
            // backstop; this poll just makes the common case update promptly.
            _ = PollAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _context.Logger.LogError(
                ex,
                "Deactivation failed for {RoleName} on tenant {TenantId}.",
                role.RoleName, _tenant.TenantId);
            await NotifyOperationErrorAsync("Deactivation", role, ExtractHeadline(ex), ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task NotifyActivationErrorAsync(
        UnifiedEligibleRole role,
        string reason,
        Exception? ex,
        CancellationToken cancellationToken)
        => NotifyOperationErrorAsync("Activation", role, reason, ex, cancellationToken);

    private async Task NotifyOperationErrorAsync(
        string operation,
        UnifiedEligibleRole role,
        string reason,
        Exception? ex,
        CancellationToken cancellationToken)
    {
        // Error-severity InformationRequest = red accent stripe. Message is
        // kept terse on purpose — the verbose context (response body, stack
        // trace, request URI, status code) flows into Details and stays
        // collapsed by default so the toast doesn't dwarf the screen.
        await _context.Notifier.ShowAsync(
            new InformationRequest(
                Title: $"{operation} failed: {role.RoleName}",
                Message: reason)
            {
                Severity = NotificationSeverity.Error,
                Details = ex is null ? null : BuildExceptionDetails(ex),
            },
            cancellationToken).ConfigureAwait(false);
    }

    // Single-line summary surfaced in the notification's Message slot. For
    // Graph/ARM HTTP failures the exception message is
    //   "Graph POST {uri} returned {code} {reason}. Body: {body}"
    // so we strip the trailing ". Body: …" and keep just the headline; the
    // body is rendered separately under Details.
    private static string ExtractHeadline(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        var bodyIdx = message.IndexOf(". Body: ", StringComparison.Ordinal);
        return bodyIdx > 0
            ? message[..bodyIdx]
            : message;
    }

    // Builds the collapsible Details rows shown beneath Message. Order
    // matters — the most actionable fields (status, body) come first; the
    // diagnostic fields (stack trace, inner) come last so they don't push
    // the useful info off-screen on smaller notifications.
    private static List<NotificationDetail> BuildExceptionDetails(Exception ex)
    {
        var rows = new List<NotificationDetail>();
        var fullMessage = ex.Message ?? string.Empty;

        if (ex is HttpRequestException http && http.StatusCode is { } status)
        {
            rows.Add(new NotificationDetail("Status", $"{(int)status} {status}"));
        }

        var bodyIdx = fullMessage.IndexOf("Body: ", StringComparison.Ordinal);
        if (bodyIdx >= 0)
        {
            var body = fullMessage[(bodyIdx + "Body: ".Length)..];
            rows.Add(new NotificationDetail("Response body", body));
        }

        rows.Add(new NotificationDetail("Type", ex.GetType().FullName ?? ex.GetType().Name));

        if (ex.InnerException is { } inner)
        {
            rows.Add(new NotificationDetail("Inner", $"{inner.GetType().Name}: {inner.Message}"));
        }

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            rows.Add(new NotificationDetail("Stack trace", ex.StackTrace!));
        }

        return rows;
    }

    internal static string FormatDuration(TimeSpan d)
    {
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min";
        if (d.TotalHours < 24) return d.Minutes == 0 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalHours}h {d.Minutes}m";
        return $"{(int)d.TotalDays}d";
    }

    // Countdown label for an activation's end time, computed when the menu is
    // built (the host rebuilds every item on each tray click, so it is accurate
    // at open and does not tick). Returns null when the end time has already
    // passed — callers fall back to the bare "active" marker rather than ever
    // rendering a negative duration.
    internal static string? FormatRemaining(DateTimeOffset end)
    {
        var remaining = end - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return null;
        if (remaining < TimeSpan.FromMinutes(1)) return "< 1m left";
        if (remaining.TotalHours < 1) return $"{(int)remaining.TotalMinutes}m left";
        if (remaining.TotalDays < 1)
        {
            return remaining.Minutes == 0
                ? $"{(int)remaining.TotalHours}h left"
                : $"{(int)remaining.TotalHours}h {remaining.Minutes}m left";
        }
        return $"{(int)remaining.TotalDays}d left";
    }

    private async Task<string?> GetPrincipalIdAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_cachedPrincipalId)) return _cachedPrincipalId;
        try
        {
            _cachedPrincipalId = await _graph.GetSignedInUserIdAsync(ct).ConfigureAwait(false);
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
            var schedules = await _graph.ListEligibleRolesAsync(principalId, ct).ConfigureAwait(false);

            // Collapsed before the caps are attached, so the policy join runs
            // once per distinct row rather than once per grant path.
            var roles = EligibleRoleDeduplicator.Deduplicate(schedules
                .Where(s => !string.IsNullOrWhiteSpace(s.RoleDefinitionId))
                .Select(s => new UnifiedEligibleRole(
                    Source: PimSource.EntraId,
                    RoleName: s.RoleDefinition?.DisplayName ?? "(unknown role)",
                    RoleDefinitionId: s.RoleDefinitionId!,
                    ScopeDisplay: EntraDirectoryScope.DisplayFor(s.DirectoryScopeId),
                    ArmScope: null,
                    EligibilityId: s.Id,
                    MemberType: s.MemberType,
                    DirectoryScopeId: s.DirectoryScopeId)));

            return await AttachEntraCapsAsync(roles, ct).ConfigureAwait(false);
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

    // What one provider contributes to a poll. Eligibility and active
    // assignments are fetched together per provider: ARM's come from the same
    // subscription list (so it is not enumerated twice), and a group's come from
    // two calls against the same root that only make sense as a pair — an active
    // row is meaningless without the eligible row it grays out.
    private sealed record SourcePollResult(
        List<UnifiedEligibleRole> Roles,
        List<ActiveRoleAssignment> ActiveAssignments)
    {
        public static SourcePollResult Empty() => new(new(), new());
    }

    private async Task<SourcePollResult> FetchArmAsync(string principalId, CancellationToken ct)
    {
        try
        {
            var subs = await _arm.ListSubscriptionsAsync(ct).ConfigureAwait(false);
            if (subs.Count == 0) return SourcePollResult.Empty();

            var scopes = subs
                .Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId))
                .Select(s => $"/subscriptions/{s.SubscriptionId}")
                .ToList();
            if (scopes.Count == 0) return SourcePollResult.Empty();

            var schedules = await _arm.ListEligibleRolesAsync(principalId, scopes, ct).ConfigureAwait(false);

            // One management-group-scoped eligibility comes back once per
            // subscription beneath it (the fan-out queries each subscription and
            // ARM includes inherited eligibilities), so the collapse happens
            // before the caps are attached — same key the policy lookup uses.
            var roles = EligibleRoleDeduplicator.Deduplicate(schedules
                .Where(s => !string.IsNullOrWhiteSpace(s.Properties?.RoleDefinitionId))
                .Select(s => new UnifiedEligibleRole(
                    Source: PimSource.AzureRbac,
                    RoleName: s.Properties!.ExpandedProperties?.RoleDefinition?.DisplayName ?? "(unknown role)",
                    RoleDefinitionId: s.Properties.RoleDefinitionId!,
                    ScopeDisplay: s.Properties.ExpandedProperties?.Scope?.DisplayName ?? s.Properties.Scope ?? "(unknown scope)",
                    ArmScope: s.Properties.Scope,
                    EligibilityId: s.Id,
                    MemberType: s.Properties.MemberType)));

            var actives = await FetchArmActiveAssignmentsAsync(principalId, scopes, ct).ConfigureAwait(false);
            return new SourcePollResult(await AttachArmCapsAsync(roles, ct).ConfigureAwait(false), actives);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return SourcePollResult.Empty(); }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "ARM eligible-role fetch failed for tenant {TenantId}; continuing with Graph only.",
                _tenant.TenantId);
            return SourcePollResult.Empty();
        }
    }

    // PIM for Groups. No principal id is passed: Graph's
    // filterByCurrentUser(on='principal') resolves the signed-in user
    // server-side for both the eligible and the active list.
    //
    // A group row's "role" is its access id — RoleName is the display form
    // ("Member" / "Owner") and RoleDefinitionId the wire form ("member" /
    // "owner"), so the row reads "Member (Contoso SQL Admins)" with the group's
    // display name in the scope slot.
    private async Task<SourcePollResult> FetchGroupsAsync(CancellationToken ct)
    {
        try
        {
            var eligibilities = await _groups.ListEligibleGroupsAsync(ct).ConfigureAwait(false);

            // Collapsed before the caps are attached, so the policy join runs
            // once per distinct row rather than once per grant path — the same
            // multi-path duplication that affects directory roles applies here
            // (a group can be reachable through more than one eligibility).
            var roles = EligibleRoleDeduplicator.Deduplicate(eligibilities
                .Where(e => !string.IsNullOrWhiteSpace(e.GroupId))
                .Select(e => new UnifiedEligibleRole(
                    Source: PimSource.EntraGroup,
                    RoleName: GroupAccess.DisplayFor(e.AccessId),
                    RoleDefinitionId: GroupAccess.Normalize(e.AccessId),
                    // The client guarantees a usable display name here, falling
                    // back to the group id when Graph would not give one up.
                    ScopeDisplay: e.Group?.DisplayName ?? e.GroupId!,
                    ArmScope: null,
                    EligibilityId: e.Id,
                    MemberType: e.MemberType,
                    DirectoryScopeId: null,
                    GroupId: e.GroupId)));

            var actives = await FetchGroupActiveAssignmentsAsync(ct).ConfigureAwait(false);
            return new SourcePollResult(await AttachGroupCapsAsync(roles, ct).ConfigureAwait(false), actives);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return SourcePollResult.Empty(); }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "PIM for Groups eligible-access fetch failed for tenant {TenantId}; continuing with Entra ID and Azure RBAC only.",
                _tenant.TenantId);
            return SourcePollResult.Empty();
        }
    }

    private async Task<List<ActiveRoleAssignment>> FetchGroupActiveAssignmentsAsync(CancellationToken ct)
    {
        try
        {
            var actives = await _groups.ListActiveGroupAssignmentsAsync(ct).ConfigureAwait(false);
            return actives
                .Where(a => !string.IsNullOrWhiteSpace(a.GroupId))
                .Select(a => new ActiveRoleAssignment(
                    Source: PimSource.EntraGroup,
                    RoleName: GroupAccess.DisplayFor(a.AccessId),
                    RoleDefinitionId: GroupAccess.Normalize(a.AccessId),
                    // Scope stays null: a group assignment is matched on GroupId,
                    // and the ARM scope-prefix logic must never see a value here.
                    Scope: null,
                    // Flat on this resource, unlike the request shapes where it
                    // nests under scheduleInfo.expiration. Null means permanent.
                    EndDateTime: a.EndDateTime,
                    GroupId: a.GroupId))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            // Caught separately from the eligibility read so a failure here
            // still leaves the group rows listed (just not grayed out).
            _context.Logger.LogWarning(
                ex,
                "PIM for Groups active-access fetch failed for tenant {TenantId}; group rows will not gray out this cycle.",
                _tenant.TenantId);
            return new();
        }
    }

    // One request per group — PIM for Groups has no tenant-wide bulk policy
    // form, and each request returns that group's member and owner policy
    // together. Only groups that appeared in the eligibility list are asked
    // about, so the fan-out is bounded by what the user can actually activate.
    private async Task<List<UnifiedEligibleRole>> AttachGroupCapsAsync(
        List<UnifiedEligibleRole> roles, CancellationToken ct)
    {
        var groupIds = roles
            .Select(r => r.GroupId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groupIds.Count == 0) return roles;

        IReadOnlyDictionary<GroupRolePolicyKey, RolePolicy>? policies = null;
        try
        {
            policies = await _groups.GetGroupPoliciesAsync(groupIds, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "PIM for Groups policy read failed for tenant {TenantId}; group activation durations fall back to the last known caps.",
                _tenant.TenantId);
        }

        for (var i = 0; i < roles.Count; i++)
        {
            TimeSpan? cap = null;
            if (policies is not null
                && policies.TryGetValue(
                    GroupRolePolicyKey.For(roles[i].GroupId, roles[i].RoleDefinitionId),
                    out var policy))
            {
                cap = policy.MaxActivationDuration;
            }
            roles[i] = roles[i] with { MaxActivationDuration = CarryForwardCap(roles[i], cap) };
        }
        return roles;
    }

    // One request per poll cycle for every directory-scoped role's policy — not
    // one per role. A failure (403 for a user without a policy-reading directory
    // role, or any transport error) leaves the caps at their last known value,
    // and Entra roles then clamp to the service's documented 8-hour ceiling.
    private async Task<List<UnifiedEligibleRole>> AttachEntraCapsAsync(
        List<UnifiedEligibleRole> roles, CancellationToken ct)
    {
        if (roles.Count == 0) return roles;

        IReadOnlyDictionary<string, RolePolicy>? policies = null;
        try
        {
            policies = await _graph.GetRolePoliciesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "Entra PIM policy read failed for tenant {TenantId}; activation durations fall back to the last known caps.",
                _tenant.TenantId);
        }

        for (var i = 0; i < roles.Count; i++)
        {
            TimeSpan? cap = null;
            if (policies is not null
                && policies.TryGetValue(roles[i].RoleDefinitionId, out var policy))
            {
                cap = policy.MaxActivationDuration;
            }
            roles[i] = roles[i] with { MaxActivationDuration = CarryForwardCap(roles[i], cap) };
        }
        return roles;
    }

    // Read at the scopes the user actually holds eligibility on (typically a
    // handful) rather than every subscription in the tenant, and in one request
    // per scope covering all roles there — the effective rules come back inline
    // on the policy assignments, so no per-role follow-up is needed.
    private async Task<List<UnifiedEligibleRole>> AttachArmCapsAsync(
        List<UnifiedEligibleRole> roles, CancellationToken ct)
    {
        var policyScopes = roles
            .Select(r => r.ArmScope)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (policyScopes.Count == 0) return roles;

        IReadOnlyDictionary<ArmRolePolicyKey, RolePolicy>? policies = null;
        try
        {
            policies = await _arm.GetRolePoliciesAsync(policyScopes, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "ARM PIM policy read failed for tenant {TenantId}; Azure RBAC activation durations fall back to the last known caps.",
                _tenant.TenantId);
        }

        for (var i = 0; i < roles.Count; i++)
        {
            TimeSpan? cap = null;
            if (policies is not null
                && policies.TryGetValue(
                    ArmRolePolicyKey.For(roles[i].ArmScope, roles[i].RoleDefinitionId),
                    out var policy))
            {
                cap = policy.MaxActivationDuration;
            }
            roles[i] = roles[i] with { MaxActivationDuration = CarryForwardCap(roles[i], cap) };
        }
        return roles;
    }

    // Policy reads are best-effort: a user who holds none of the directory
    // roles that permit reading PIM policies gets a 403, and that must not
    // break the menu or block activation. When the cap for a role cannot be
    // read this cycle, keep whatever the last successful cycle knew rather
    // than downgrading a known cap to "unknown".
    //
    // GroupId is part of the match, not an optional extra: a group row's
    // RoleDefinitionId is only ever "member" or "owner", so without it every
    // group row in the tenant would inherit the first one's cap.
    private TimeSpan? CarryForwardCap(UnifiedEligibleRole role, TimeSpan? fetched)
    {
        if (fetched is not null) return fetched;

        foreach (var previous in _lastSnapshot)
        {
            if (previous.Source == role.Source
                && string.Equals(previous.RoleDefinitionId, role.RoleDefinitionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.ArmScope, role.ArmScope, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.GroupId, role.GroupId, StringComparison.OrdinalIgnoreCase))
            {
                return previous.MaxActivationDuration;
            }
        }
        return null;
    }
}
