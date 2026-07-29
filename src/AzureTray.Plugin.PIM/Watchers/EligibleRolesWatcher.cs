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
using AzureTray.Plugin.PIM.Policies;

namespace AzureTray.Plugin.PIM.Watchers;

// One watcher per tenant. Polls eligible roles from both Graph (Entra ID) and
// ARM (Azure RBAC) on a slow cadence (30 minutes by default — eligibility
// changes infrequently). The user can force an immediate refresh from the
// tray menu's "↻ <Tenant>" entry. Activation is initiated by clicking a role:
// duration prompt → justification prompt → call the matching API.
internal sealed class EligibleRolesWatcher
{
    private readonly IGraphPimClient _graph;
    private readonly IArmPimClient _arm;
    private readonly IPluginContext _context;
    private readonly PluginTenant _tenant;
    private readonly TimeSpan _interval;
    private readonly PendingActivationStore _pendingActivations;

    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private UnifiedEligibleRole[] _lastSnapshot = Array.Empty<UnifiedEligibleRole>();
    private ActiveRoleAssignment[] _activeAssignments = Array.Empty<ActiveRoleAssignment>();
    private IReadOnlySet<string> _relevantSubscriptionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string? _cachedPrincipalId;

    public EligibleRolesWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        IPluginContext context,
        PluginTenant tenant,
        TimeSpan interval,
        PendingActivationStore pendingActivations)
    {
        _graph = graph;
        _arm = arm;
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
    // fetched per provider (Graph for Entra ID, ARM for Azure RBAC). The menu
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
            var graphActiveTask = FetchGraphActiveAssignmentsAsync(principalId, cancellationToken);

            var graphRoles = await graphTask.ConfigureAwait(false);
            var arm = await armTask.ConfigureAwait(false);
            var graphActives = await graphActiveTask.ConfigureAwait(false);

            _lastSnapshot = graphRoles.Concat(arm.Roles).ToArray();
            _activeAssignments = graphActives.Concat(arm.ActiveAssignments).ToArray();
            _relevantSubscriptionIds = ExtractSubscriptionIds(arm.Roles);
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
                    Message: $"on {role.ScopeDisplay}. How long?",
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
                    Placeholder: "Required"),
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

    // Eligibility and active assignments come from the same subscription list,
    // so they are fetched together rather than enumerating subscriptions twice.
    private sealed record ArmPollResult(
        List<UnifiedEligibleRole> Roles,
        List<ActiveRoleAssignment> ActiveAssignments)
    {
        public static ArmPollResult Empty() => new(new(), new());
    }

    private async Task<ArmPollResult> FetchArmAsync(string principalId, CancellationToken ct)
    {
        try
        {
            var subs = await _arm.ListSubscriptionsAsync(ct).ConfigureAwait(false);
            if (subs.Count == 0) return ArmPollResult.Empty();

            var scopes = subs
                .Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId))
                .Select(s => $"/subscriptions/{s.SubscriptionId}")
                .ToList();
            if (scopes.Count == 0) return ArmPollResult.Empty();

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
            return new ArmPollResult(await AttachArmCapsAsync(roles, ct).ConfigureAwait(false), actives);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return ArmPollResult.Empty(); }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "ARM eligible-role fetch failed for tenant {TenantId}; continuing with Graph only.",
                _tenant.TenantId);
            return ArmPollResult.Empty();
        }
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
    private TimeSpan? CarryForwardCap(UnifiedEligibleRole role, TimeSpan? fetched)
    {
        if (fetched is not null) return fetched;

        foreach (var previous in _lastSnapshot)
        {
            if (previous.Source == role.Source
                && string.Equals(previous.RoleDefinitionId, role.RoleDefinitionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.ArmScope, role.ArmScope, StringComparison.OrdinalIgnoreCase))
            {
                return previous.MaxActivationDuration;
            }
        }
        return null;
    }
}
