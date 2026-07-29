using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Policies;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Attachment of policy caps onto the eligible-role snapshot. Policy reads are
// best-effort — a user holding none of the directory roles that permit reading
// PIM policies gets a 403 — so the failure paths matter as much as the happy
// one: the eligible list must survive, and a cap already known must not be
// downgraded to "unknown" by a failed refresh.
public sealed class EligibleRolesWatcherCapTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    private static readonly PluginTenant Tenant = new("tenant-1", "Contoso");

    private const string SubScope = "/subscriptions/sub-1";
    private const string RgScope = "/subscriptions/sub-2/resourceGroups/rg-a";

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- happy path -------------------------------------------------------

    [Fact]
    public async Task PollAsync_AttachesEntraCap_FromTheGraphPolicyRead()
    {
        var graph = NewGraph(eligible: new[] { GraphEligible("Owner", "role-owner") });
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(EntraPolicies(("role-owner", TimeSpan.FromHours(4))));

        var watcher = NewWatcher(graph, NewArm());
        await watcher.PollAsync(CancellationToken.None);

        var role = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal(TimeSpan.FromHours(4), role.MaxActivationDuration);
    }

    // A role with no policy entry is "unknown", not "unrestricted".
    [Fact]
    public async Task PollAsync_RoleAbsentFromThePolicyRead_HasNoCap()
    {
        var graph = NewGraph(eligible: new[] { GraphEligible("Owner", "role-owner") });
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(EntraPolicies(("some-other-role", TimeSpan.FromHours(1))));

        var watcher = NewWatcher(graph, NewArm());
        await watcher.PollAsync(CancellationToken.None);

        Assert.Null(Assert.Single(watcher.CurrentEligibleRoles).MaxActivationDuration);
    }

    [Fact]
    public async Task PollAsync_AttachesArmCap_KeyedByScopeAndRoleDefinition()
    {
        var graph = NewGraph();
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1"), ArmSub("sub-2") },
            eligible: new[]
            {
                ArmEligible("Reader", "role-reader", SubScope),
                ArmEligible("Reader", "role-reader", RgScope),
            });
        arm.GetRolePoliciesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ArmPolicies(
                (SubScope, "role-reader", TimeSpan.FromHours(8)),
                (RgScope, "role-reader", TimeSpan.FromHours(2))));

        var watcher = NewWatcher(graph, arm);
        await watcher.PollAsync(CancellationToken.None);

        // Same role definition, two scopes, two different caps — the scope is
        // half the key, so they must not collide.
        Assert.Equal(
            TimeSpan.FromHours(8),
            Assert.Single(watcher.CurrentEligibleRoles, r => r.ArmScope == SubScope).MaxActivationDuration);
        Assert.Equal(
            TimeSpan.FromHours(2),
            Assert.Single(watcher.CurrentEligibleRoles, r => r.ArmScope == RgScope).MaxActivationDuration);
    }

    // Policies are read at the scopes the user actually holds eligibility on —
    // not once per subscription in the tenant, which is the expensive mistake
    // this replaced.
    [Fact]
    public async Task PollAsync_ReadsArmPoliciesAtEligibilityScopes_NotOncePerSubscription()
    {
        var capturedScopes = new List<List<string>>();
        var graph = NewGraph();
        var arm = NewArm(
            // Three subscriptions in the tenant...
            subscriptions: new[] { ArmSub("sub-1"), ArmSub("sub-2"), ArmSub("sub-3") },
            // ...but eligibility at only two distinct scopes, one of them twice.
            eligible: new[]
            {
                ArmEligible("Reader", "role-reader", SubScope),
                ArmEligible("Contributor", "role-contributor", SubScope),
                ArmEligible("Reader", "role-reader", RgScope),
            });
        arm.GetRolePoliciesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<ArmRolePolicyKey, RolePolicy>>(call =>
            {
                capturedScopes.Add(call.Arg<IEnumerable<string>>().ToList());
                return new Dictionary<ArmRolePolicyKey, RolePolicy>();
            });

        var watcher = NewWatcher(graph, arm);
        await watcher.PollAsync(CancellationToken.None);

        await arm.Received(1).GetRolePoliciesAsync(
            Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        var scopes = Assert.Single(capturedScopes);
        Assert.Equal(2, scopes.Count);
        Assert.Contains(SubScope, scopes);
        Assert.Contains(RgScope, scopes);
        Assert.DoesNotContain(scopes, s => s.Contains("sub-3", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PollAsync_NoEntraRoles_SkipsTheEntraPolicyRead()
    {
        var graph = NewGraph();
        var watcher = NewWatcher(graph, NewArm());

        await watcher.PollAsync(CancellationToken.None);

        await graph.DidNotReceive().GetRolePoliciesAsync(Arg.Any<CancellationToken>());
    }

    // ---- degradation ------------------------------------------------------

    // The 403 case. The eligible list is the menu's whole content, so it must
    // survive; and the cap the previous cycle established must be carried
    // forward rather than nulled, or the row's "max 2h" hint would flicker off
    // and the prompt would silently widen back to the standard steps.
    [Fact]
    public async Task PollAsync_EntraPolicyReadForbidden_KeepsRolesAndCarriesTheCapForward()
    {
        var calls = 0;
        var graph = NewGraph(eligible: new[] { GraphEligible("Owner", "role-owner") });
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, RolePolicy>>(_ =>
            {
                calls++;
                return calls == 1
                    ? EntraPolicies(("role-owner", TimeSpan.FromHours(4)))
                    : throw Forbidden();
            });

        var watcher = NewWatcher(graph, NewArm());

        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(4), Assert.Single(watcher.CurrentEligibleRoles).MaxActivationDuration);

        await watcher.PollAsync(CancellationToken.None);

        var role = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal("Owner", role.RoleName);
        Assert.Equal(TimeSpan.FromHours(4), role.MaxActivationDuration);
    }

    [Fact]
    public async Task PollAsync_ArmPolicyReadForbidden_KeepsRolesAndCarriesTheCapForward()
    {
        var calls = 0;
        var graph = NewGraph();
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1") },
            eligible: new[] { ArmEligible("Reader", "role-reader", SubScope) });
        arm.GetRolePoliciesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<ArmRolePolicyKey, RolePolicy>>(_ =>
            {
                calls++;
                return calls == 1
                    ? ArmPolicies((SubScope, "role-reader", TimeSpan.FromHours(2)))
                    : throw Forbidden();
            });

        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(watcher.CurrentEligibleRoles).MaxActivationDuration);

        await watcher.PollAsync(CancellationToken.None);

        var role = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal("Reader", role.RoleName);
        Assert.Equal(TimeSpan.FromHours(2), role.MaxActivationDuration);
    }

    // A failing policy read on the very first cycle has nothing to carry
    // forward: the cap is unknown, and the roles are still listed.
    [Fact]
    public async Task PollAsync_PolicyReadForbiddenOnTheFirstCycle_ListsRolesWithNoCap()
    {
        var graph = NewGraph(eligible: new[] { GraphEligible("Owner", "role-owner") });
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, RolePolicy>>(_ => throw Forbidden());

        var watcher = NewWatcher(graph, NewArm());
        await watcher.PollAsync(CancellationToken.None);

        var role = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal("Owner", role.RoleName);
        Assert.Null(role.MaxActivationDuration);
    }

    // ---- the clamp where it is visible: the activation prompt -------------

    // The whole point of reading the cap: the prompt must not offer a duration
    // the service will reject, and the pick must round-trip back to the exact
    // TimeSpan behind the chosen label.
    [Fact]
    public async Task HandleActivationAsync_OffersOnlyDurationsWithinTheCap_AndActivatesForThePick()
    {
        ChoiceRequest? prompt = null;
        var graph = NewGraph(eligible: new[] { GraphEligible("Owner", "role-owner") });
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(EntraPolicies(("role-owner", TimeSpan.FromHours(2))));
        graph.ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest("req-1", "Provisioned"));

        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns<NotificationResult>(call =>
            {
                prompt = call.Arg<ChoiceRequest>();
                return new ChoiceResult("2 hours", null);
            });
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TextInputResult("incident #42"));

        var watcher = NewWatcher(graph, NewArm(), notifier);
        await watcher.PollAsync(CancellationToken.None);

        await watcher.HandleActivationAsync(
            Assert.Single(watcher.CurrentEligibleRoles), CancellationToken.None);

        Assert.NotNull(prompt);
        Assert.Equal(new[] { "1 hour", "2 hours" }, prompt!.Choices);
        await graph.Received(1).ActivateRoleAsync(
            "prin-1", "role-owner", "/", TimeSpan.FromHours(2), "incident #42", Arg.Any<CancellationToken>());
    }

    // A label the current (clamped) list never offered — a stale prompt, or a
    // notifier echoing something else — must abandon the activation rather than
    // send a duration above the cap for the service to reject.
    [Fact]
    public async Task HandleActivationAsync_LabelAboveTheCap_DoesNotActivate()
    {
        var graph = NewGraph(eligible: new[] { GraphEligible("Owner", "role-owner") });
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(EntraPolicies(("role-owner", TimeSpan.FromHours(2))));

        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChoiceResult("8 hours", null));

        var watcher = NewWatcher(graph, NewArm(), notifier);
        await watcher.PollAsync(CancellationToken.None);

        await watcher.HandleActivationAsync(
            Assert.Single(watcher.CurrentEligibleRoles), CancellationToken.None);

        await graph.DidNotReceive().ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- cache round-trip -------------------------------------------------

    [Fact]
    public async Task PollAsync_ThenStart_RoundTripsTheCapThroughTheCacheFile()
    {
        var graph = NewGraph(eligible: new[] { GraphEligible("Owner", "role-owner") });
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(EntraPolicies(("role-owner", TimeSpan.FromMinutes(90))));

        var writer = NewWatcher(graph, NewArm());
        await writer.PollAsync(CancellationToken.None);

        var reader = NewWatcher(NewGraph(), NewArm());
        reader.Start(new CancellationToken(canceled: true));
        await reader.StopAsync();

        Assert.Equal(
            TimeSpan.FromMinutes(90),
            Assert.Single(reader.CurrentEligibleRoles).MaxActivationDuration);
    }

    // Caches written before the cap existed have no MaxActivationDuration
    // member. That must load as "unknown" rather than throwing or dropping the
    // cached eligibility.
    [Fact]
    public async Task Start_LegacyCacheWithoutTheCapMember_LoadsTheRoleWithAnUnknownCap()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(
            Path.Combine(_dataDir, $"eligible-roles-{Tenant.TenantId}.json"),
            """
            {
              "Roles": [
                {
                  "Source": 0,
                  "RoleName": "Owner",
                  "RoleDefinitionId": "role-owner",
                  "ScopeDisplay": "Entra ID directory",
                  "ArmScope": null,
                  "EligibilityId": "elig-1"
                }
              ],
              "RelevantSubscriptionIds": []
            }
            """);

        var watcher = NewWatcher(NewGraph(), NewArm());
        watcher.Start(new CancellationToken(canceled: true));
        await watcher.StopAsync();

        var role = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal("Owner", role.RoleName);
        Assert.Null(role.MaxActivationDuration);
    }

    // ---- builders ---------------------------------------------------------

    private static HttpRequestException Forbidden()
        => new(
            "Graph GET policies/roleManagementPolicyAssignments returned 403 Forbidden. Body: {\"error\":{\"code\":\"Authorization_RequestDenied\"}}",
            inner: null,
            statusCode: HttpStatusCode.Forbidden);

    // Returned as the bare dictionary rather than a Task: passing a Task to
    // NSubstitute's Returns makes the T-vs-Task<T> overloads ambiguous.
    private static Dictionary<string, RolePolicy> EntraPolicies(
        params (string RoleDefinitionId, TimeSpan Cap)[] entries)
    {
        var dict = new Dictionary<string, RolePolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var (roleDefinitionId, cap) in entries)
        {
            dict[roleDefinitionId] = new RolePolicy(ApprovalRequired: null, MaxActivationDuration: cap);
        }
        return dict;
    }

    private static Dictionary<ArmRolePolicyKey, RolePolicy> ArmPolicies(
        params (string Scope, string RoleDefinitionId, TimeSpan Cap)[] entries)
    {
        var dict = new Dictionary<ArmRolePolicyKey, RolePolicy>();
        foreach (var (scope, roleDefinitionId, cap) in entries)
        {
            dict[ArmRolePolicyKey.For(scope, roleDefinitionId)] =
                new RolePolicy(ApprovalRequired: null, MaxActivationDuration: cap);
        }
        return dict;
    }

    private static IGraphPimClient NewGraph(IReadOnlyList<EntraEligibilitySchedule>? eligible = null)
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(eligible ?? Array.Empty<EntraEligibilitySchedule>());
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(EntraPolicies());
        return graph;
    }

    private static IArmPimClient NewArm(
        IReadOnlyList<ArmSubscription>? subscriptions = null,
        IReadOnlyList<ArmEligibilitySchedule>? eligible = null)
    {
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(subscriptions ?? Array.Empty<ArmSubscription>());
        arm.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(eligible ?? Array.Empty<ArmEligibilitySchedule>());
        arm.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmRoleAssignmentScheduleInstance>());
        arm.GetRolePoliciesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ArmPolicies());
        return arm;
    }

    private static EntraEligibilitySchedule GraphEligible(string roleDisplayName, string roleDefId)
        => new(
            Id: $"elig-{roleDefId}",
            PrincipalId: "prin-1",
            RoleDefinitionId: roleDefId,
            DirectoryScopeId: "/",
            StartDateTime: DateTimeOffset.UtcNow,
            EndDateTime: null,
            MemberType: "Direct",
            Principal: new EntraPrincipal("prin-1", "Alice", null),
            RoleDefinition: new EntraRoleDefinition(roleDefId, roleDisplayName, null));

    private static EntraScheduleRequest GraphRequest(string id, string status)
        => new(
            Id: id,
            Status: status,
            Action: "selfActivate",
            PrincipalId: "prin-1",
            RoleDefinitionId: "role-owner",
            DirectoryScopeId: "/",
            Justification: "incident #42",
            CreatedDateTime: DateTimeOffset.UtcNow,
            ApprovalId: null,
            RequestType: null,
            Principal: null,
            RoleDefinition: null,
            ScheduleInfo: null);

    private static ArmSubscription ArmSub(string id)
        => new($"/subscriptions/{id}", id, $"Sub {id}", "Enabled");

    private static ArmEligibilitySchedule ArmEligible(string roleDisplayName, string roleDefId, string scope)
        => new(
            Id: $"{scope}/providers/Microsoft.Authorization/roleEligibilitySchedules/elig-{roleDefId}",
            Name: $"elig-{roleDefId}",
            Properties: new ArmEligibilityProperties(
                PrincipalId: "prin-1",
                RoleDefinitionId: roleDefId,
                Scope: scope,
                Status: "Active",
                MemberType: "Direct",
                StartDateTime: DateTimeOffset.UtcNow,
                EndDateTime: null,
                ExpandedProperties: new ArmExpandedProperties(
                    Principal: new ArmPrincipalDto("prin-1", "Alice", "User", null),
                    RoleDefinition: new ArmRoleDefinitionDto(roleDefId, roleDisplayName, null),
                    Scope: new ArmScopeDto(scope, "Dev sub", "subscription"))));

    private EligibleRolesWatcher NewWatcher(
        IGraphPimClient graph, IArmPimClient arm, INotifier? notifier = null)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<EligibleRolesWatcherCapTests>.Instance);
        ctx.Notifier.Returns(notifier ?? Substitute.For<INotifier>());
        ctx.DataDir.Returns(_dataDir);
        ctx.Tenants.Returns(new List<PluginTenant> { Tenant });

        return new EligibleRolesWatcher(
            graph, arm, ctx, Tenant,
            TimeSpan.FromMilliseconds(50),
            new PendingActivationStore(ctx, Tenant));
    }
}
