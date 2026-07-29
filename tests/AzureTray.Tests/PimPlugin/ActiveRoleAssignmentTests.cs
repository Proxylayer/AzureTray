using System;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

public sealed class ActiveRoleAssignmentTests
{
    [Fact]
    public void Matches_OnRoleDefinitionId_EvenWhenDisplayNamesDiffer()
    {
        var active = Active(PimSource.EntraId, "Global Administrator", "role-ga", scope: "/");

        Assert.True(active.Matches(EntraRole("Company Administrator", "role-ga")));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveOnRoleDefinitionId()
    {
        var active = Active(PimSource.EntraId, "Owner", "ROLE-OWNER", scope: "/");

        Assert.True(active.Matches(EntraRole("Owner", "role-owner")));
    }

    [Fact]
    public void DoesNotMatch_WhenRoleDefinitionIdsDiffer()
    {
        var active = Active(PimSource.EntraId, "Owner", "role-owner", scope: "/");

        Assert.False(active.Matches(EntraRole("Owner", "role-reader")));
    }

    [Fact]
    public void Matches_FallsBackToRoleName_WhenActiveHasNoRoleDefinitionId()
    {
        var active = Active(PimSource.EntraId, "Owner", roleDefinitionId: null, scope: "/");

        Assert.True(active.Matches(EntraRole("owner", "role-owner")));
        Assert.False(active.Matches(EntraRole("Reader", "role-reader")));
    }

    [Fact]
    public void Matches_FallsBackToRoleName_WhenRoleDefinitionIdIsBlank()
    {
        var active = Active(PimSource.EntraId, "Owner", roleDefinitionId: "   ", scope: "/");

        Assert.True(active.Matches(EntraRole("Owner", "role-owner")));
    }

    // The latent bug this guards: display names collide across providers
    // ("Owner" exists in both Entra ID and Azure RBAC), so an Entra activation
    // used to gray out an ARM row (and vice versa).
    [Fact]
    public void DoesNotMatch_AcrossProviders_WhenOnlyTheDisplayNameCollides()
    {
        var entraActive = Active(PimSource.EntraId, "Owner", roleDefinitionId: null, scope: "/");
        var armRow = ArmRole("Owner", "arm-role-owner", "/subscriptions/sub-1");

        Assert.False(entraActive.Matches(armRow));
    }

    [Fact]
    public void DoesNotMatch_AcrossProviders_EvenWhenRoleDefinitionIdCollides()
    {
        var armActive = Active(PimSource.AzureRbac, "Owner", "role-owner", "/subscriptions/sub-1");

        Assert.False(armActive.Matches(EntraRole("Owner", "role-owner")));
    }

    [Fact]
    public void Arm_ExactScope_Matches_CaseInsensitively()
    {
        var active = Active(PimSource.AzureRbac, "Contributor", "arm-role-contrib", "/subscriptions/SUB-1");

        Assert.True(active.Matches(ArmRole("Contributor", "arm-role-contrib", "/subscriptions/sub-1")));
    }

    [Fact]
    public void Arm_AncestorScope_Matches_ResourceGroupRow()
    {
        var active = Active(PimSource.AzureRbac, "Contributor", "arm-role-contrib", "/subscriptions/sub-1");

        Assert.True(active.Matches(
            ArmRole("Contributor", "arm-role-contrib", "/subscriptions/sub-1/resourceGroups/rg-1")));
    }

    [Fact]
    public void Arm_TrailingSlashOnScope_StillMatches()
    {
        var active = Active(PimSource.AzureRbac, "Contributor", "arm-role-contrib", "/subscriptions/sub-1/");

        Assert.True(active.Matches(ArmRole("Contributor", "arm-role-contrib", "/subscriptions/sub-1")));
    }

    [Fact]
    public void Arm_SiblingScope_DoesNotMatch()
    {
        var active = Active(
            PimSource.AzureRbac, "Contributor", "arm-role-contrib",
            "/subscriptions/sub-1/resourceGroups/rg-1");

        Assert.False(active.Matches(
            ArmRole("Contributor", "arm-role-contrib", "/subscriptions/sub-1/resourceGroups/rg-2")));
    }

    [Fact]
    public void Arm_UnrelatedSubscription_DoesNotMatch()
    {
        var active = Active(PimSource.AzureRbac, "Contributor", "arm-role-contrib", "/subscriptions/sub-1");

        Assert.False(active.Matches(ArmRole("Contributor", "arm-role-contrib", "/subscriptions/sub-2")));
    }

    // Prefix comparison must respect segment boundaries: "/subscriptions/sub-1"
    // is not an ancestor of "/subscriptions/sub-1234".
    [Fact]
    public void Arm_PrefixWithoutSegmentBoundary_DoesNotMatch()
    {
        var active = Active(PimSource.AzureRbac, "Contributor", "arm-role-contrib", "/subscriptions/sub-1");

        Assert.False(active.Matches(ArmRole("Contributor", "arm-role-contrib", "/subscriptions/sub-1234")));
    }

    // A resource-group activation does not back a subscription-wide row.
    [Fact]
    public void Arm_DescendantScope_DoesNotMatchBroaderRow()
    {
        var active = Active(
            PimSource.AzureRbac, "Contributor", "arm-role-contrib",
            "/subscriptions/sub-1/resourceGroups/rg-1");

        Assert.False(active.Matches(ArmRole("Contributor", "arm-role-contrib", "/subscriptions/sub-1")));
    }

    [Fact]
    public void Arm_MissingScopeOnEitherSide_DoesNotMatch()
    {
        var noScopeActive = Active(PimSource.AzureRbac, "Contributor", "arm-role-contrib", scope: null);
        Assert.False(noScopeActive.Matches(ArmRole("Contributor", "arm-role-contrib", "/subscriptions/sub-1")));

        var active = Active(PimSource.AzureRbac, "Contributor", "arm-role-contrib", "/subscriptions/sub-1");
        Assert.False(active.Matches(ArmRole("Contributor", "arm-role-contrib", armScope: null)));
    }

    // Entra ID is directory-scoped; the scope field must not gate the match.
    [Fact]
    public void Entra_ScopeIsIgnored()
    {
        var active = Active(PimSource.EntraId, "Owner", "role-owner", scope: "/");

        Assert.True(active.Matches(EntraRole("Owner", "role-owner")));
    }

    // ---- builders ---------------------------------------------------------

    private static ActiveRoleAssignment Active(
        PimSource source,
        string roleName,
        string? roleDefinitionId,
        string? scope,
        DateTimeOffset? endDateTime = null)
        => new(source, roleName, roleDefinitionId, scope, endDateTime);

    private static UnifiedEligibleRole EntraRole(string roleName, string roleDefinitionId)
        => new(
            Source: PimSource.EntraId,
            RoleName: roleName,
            RoleDefinitionId: roleDefinitionId,
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            EligibilityId: "elig-1");

    private static UnifiedEligibleRole ArmRole(string roleName, string roleDefinitionId, string? armScope)
        => new(
            Source: PimSource.AzureRbac,
            RoleName: roleName,
            RoleDefinitionId: roleDefinitionId,
            ScopeDisplay: "Dev (sub)",
            ArmScope: armScope,
            EligibilityId: "elig-arm-1");
}
