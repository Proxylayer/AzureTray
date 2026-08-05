using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Hydration from the on-disk eligible-roles cache. Start() is handed an
// already-cancelled token so the cache load happens without the background
// poll racing it, and every test gets its own DataDir.
public sealed class EligibleRolesWatcherCacheTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    private static readonly PluginTenant Tenant = new("tenant-1", "Contoso");

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Caches written before actives carried end times have no ActiveAssignments
    // member at all. That must load as "eligibility known, actives unknown"
    // rather than throwing or dropping the cached eligibility.
    [Fact]
    public async Task Start_LegacyCacheWithoutActiveAssignments_LoadsRolesAndTreatsActivesAsUnknown()
    {
        WriteCache("""
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
              "RelevantSubscriptionIds": [ "sub-1" ]
            }
            """);

        var watcher = NewWatcher();
        await HydrateAsync(watcher);

        Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal("Owner", watcher.CurrentEligibleRoles[0].RoleName);
        Assert.Contains("sub-1", watcher.RelevantSubscriptionIds);

        // Actives unknown: nothing is claimed active until the first poll.
        Assert.Empty(watcher.CurrentActiveAssignments);
        Assert.Null(watcher.FindActiveFor(watcher.CurrentEligibleRoles[0]));

        // Caches written before MG scopes existed load as "no known MG scopes".
        Assert.Empty(watcher.RelevantManagementGroupScopes);
    }

    [Fact]
    public async Task Start_CacheWithActiveAssignments_RestoresThemIncludingEndTime()
    {
        var end = DateTimeOffset.UtcNow.AddHours(2);
        WriteCache($$"""
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
              "ActiveAssignments": [
                {
                  "Source": 0,
                  "RoleName": "Owner",
                  "RoleDefinitionId": "role-owner",
                  "Scope": "/",
                  "EndDateTime": "{{end:O}}"
                }
              ],
              "RelevantSubscriptionIds": []
            }
            """);

        var watcher = NewWatcher();
        await HydrateAsync(watcher);

        var active = watcher.FindActiveFor(watcher.CurrentEligibleRoles[0]);
        Assert.NotNull(active);
        Assert.Equal(end, active!.EndDateTime);
    }

    [Fact]
    public async Task Start_CorruptCache_IsACacheMiss_NotAnException()
    {
        WriteCache("{ \"Roles\": [ this is not json ");

        var watcher = NewWatcher();
        await HydrateAsync(watcher);

        Assert.Empty(watcher.CurrentEligibleRoles);
        Assert.Empty(watcher.CurrentActiveAssignments);
    }

    [Fact]
    public async Task Start_CacheOfLiteralNull_IsACacheMiss_NotAnException()
    {
        WriteCache("null");

        var watcher = NewWatcher();
        await HydrateAsync(watcher);

        Assert.Empty(watcher.CurrentEligibleRoles);
    }

    [Fact]
    public async Task Start_NoCacheFile_LoadsEmpty()
    {
        var watcher = NewWatcher();
        await HydrateAsync(watcher);

        Assert.Empty(watcher.CurrentEligibleRoles);
        Assert.Empty(watcher.CurrentActiveAssignments);
    }

    // Round-trip through the real serializer: what PollAsync saves is what a
    // fresh watcher hydrates, actives and end times included.
    [Fact]
    public async Task PollAsync_ThenStart_RoundTripsRolesAndActivesThroughTheCache()
    {
        var end = DateTimeOffset.UtcNow.AddMinutes(90);
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync("prin-1", Arg.Any<CancellationToken>())
            .Returns(new[] { GraphSchedule("Owner", "role-owner", null) });
        graph.ListActiveRoleAssignmentsAsync("prin-1", Arg.Any<CancellationToken>())
            .Returns(new[] { GraphSchedule("Owner", "role-owner", end) });

        var writer = NewWatcher(graph);
        await writer.PollAsync(CancellationToken.None);

        var reader = NewWatcher();
        await HydrateAsync(reader);

        Assert.Single(reader.CurrentEligibleRoles);
        var active = reader.FindActiveFor(reader.CurrentEligibleRoles[0]);
        Assert.NotNull(active);
        Assert.Equal(PimSource.EntraId, active!.Source);
        Assert.Equal(end, active.EndDateTime);
    }

    // MG-scoped eligibility feeds the pending-approval fan-out, so the scope
    // set must survive the cache the same way roles and actives do.
    [Fact]
    public async Task PollAsync_ThenStart_RoundTripsManagementGroupScopesThroughTheCache()
    {
        const string mgScope = "/providers/Microsoft.Management/managementGroups/mg-1";

        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync("prin-1", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        graph.ListActiveRoleAssignmentsAsync("prin-1", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());

        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new ArmSubscription("/subscriptions/sub-1", "sub-1", "Dev", "Enabled") });
        arm.ListEligibleRolesAsync("prin-1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ArmEligibilitySchedule(
                    Id: "elig-arm-1",
                    Name: "elig-arm-1",
                    Properties: new ArmEligibilityProperties(
                        PrincipalId: "prin-1",
                        RoleDefinitionId: "role-contributor",
                        Scope: mgScope,
                        Status: "Provisioned",
                        MemberType: "Direct",
                        StartDateTime: DateTimeOffset.UtcNow,
                        EndDateTime: null,
                        ExpandedProperties: null)),
            });
        arm.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmRoleAssignmentScheduleInstance>());

        var writer = NewWatcher(graph, arm);
        await writer.PollAsync(CancellationToken.None);
        Assert.Contains(mgScope, writer.RelevantManagementGroupScopes);

        var reader = NewWatcher();
        await HydrateAsync(reader);

        Assert.Contains(mgScope, reader.RelevantManagementGroupScopes);
    }

    // ---- helpers ----------------------------------------------------------

    private string CachePath => Path.Combine(_dataDir, $"eligible-roles-{Tenant.TenantId}.json");

    private void WriteCache(string json)
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(CachePath, json);
    }

    // Start() hydrates from cache synchronously, then queues the poll loop on
    // the supplied token — a cancelled one means the loop body never runs.
    private static async Task HydrateAsync(EligibleRolesWatcher watcher)
    {
        watcher.Start(new CancellationToken(canceled: true));
        await watcher.StopAsync();
    }

    private static EntraEligibilitySchedule GraphSchedule(
        string roleDisplayName, string roleDefId, DateTimeOffset? endDateTime)
        => new(
            Id: $"elig-{roleDefId}",
            PrincipalId: "prin-1",
            RoleDefinitionId: roleDefId,
            DirectoryScopeId: "/",
            StartDateTime: DateTimeOffset.UtcNow,
            EndDateTime: endDateTime,
            MemberType: "Direct",
            Principal: new EntraPrincipal("prin-1", "Alice", null),
            RoleDefinition: new EntraRoleDefinition(roleDefId, roleDisplayName, null));

    private EligibleRolesWatcher NewWatcher(IGraphPimClient? graph = null, IArmPimClient? arm = null)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<EligibleRolesWatcherCacheTests>.Instance);
        ctx.Notifier.Returns(Substitute.For<INotifier>());
        ctx.DataDir.Returns(_dataDir);

        if (arm is null)
        {
            arm = Substitute.For<IArmPimClient>();
            arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ArmSubscription>());
            arm.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ArmEligibilitySchedule>());
            arm.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ArmRoleAssignmentScheduleInstance>());
        }

        var effectiveGraph = graph ?? Substitute.For<IGraphPimClient>();
        return new EligibleRolesWatcher(
            effectiveGraph, arm, ctx, Tenant,
            TimeSpan.FromMilliseconds(50),
            new PendingActivationStore(ctx, Tenant));
    }
}
