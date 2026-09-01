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

    // ---- PIM for Groups ---------------------------------------------------

    // A group assignment is identified by the group plus the access id. Both
    // halves are compared case-insensitively, because Graph's casing of an
    // object id or an access id is not something to depend on.
    [Fact]
    public void Group_SameGroupAndAccess_Matches_CaseInsensitively()
    {
        var active = GroupActive("GROUP-1", "Member");

        Assert.True(active.Matches(GroupRole("group-1", "member")));
    }

    // Every group row in the tenant reads "Member" or "Owner", so without the
    // group id one activation would gray out every other group's row.
    [Fact]
    public void Group_DifferentGroup_SameAccess_DoesNotMatch()
    {
        var active = GroupActive("group-1", "member");

        Assert.False(active.Matches(GroupRole("group-2", "member")));
    }

    [Fact]
    public void Group_SameGroup_DifferentAccess_DoesNotMatch()
    {
        var active = GroupActive("group-1", "member");

        Assert.False(active.Matches(GroupRole("group-1", "owner")));
    }

    // A row or an assignment with no group id cannot be matched to anything:
    // there is no scope left to compare, and guessing would gray out the wrong
    // row.
    [Fact]
    public void Group_MissingGroupIdOnEitherSide_DoesNotMatch()
    {
        Assert.False(GroupActive(groupId: null, "member").Matches(GroupRole("group-1", "member")));
        Assert.False(GroupActive("group-1", "member").Matches(GroupRole(groupId: null, "member")));
    }

    // The cross-provider guard, group edition: a group named after a directory
    // role (or an access id that collides with a role definition id) must not
    // cross-match in either direction.
    [Fact]
    public void Group_AndEntraDirectoryRow_DoNotCrossMatch_EvenWhenNamesCoincide()
    {
        var groupActive = GroupActive("group-1", "member", roleName: "Member");
        var entraActive = Active(PimSource.EntraId, "Member", "member", scope: "/");

        Assert.False(groupActive.Matches(EntraRole("Member", "member")));
        Assert.False(entraActive.Matches(GroupRole("group-1", "member", roleName: "Member")));
    }

    [Fact]
    public void Group_AndArmRow_DoNotCrossMatch()
    {
        var groupActive = GroupActive("group-1", "owner", roleName: "Owner");
        var armActive = Active(PimSource.AzureRbac, "Owner", "owner", "/subscriptions/sub-1");

        Assert.False(groupActive.Matches(ArmRole("Owner", "owner", "/subscriptions/sub-1")));
        Assert.False(armActive.Matches(GroupRole("group-1", "owner", roleName: "Owner")));
    }

    // ARM's ancestor-prefix logic must never run for a group: there is no
    // inheritance between groups, and a group assignment carries no Scope at
    // all — a stray one must not become a prefix match.
    [Fact]
    public void Group_ScopeIsIgnored_AndTheAncestorPrefixRuleDoesNotApply()
    {
        var withStrayScope = new ActiveRoleAssignment(
            PimSource.EntraGroup, "Member", "member", Scope: "/subscriptions/sub-1",
            EndDateTime: null, GroupId: "group-1");

        // The stray scope neither helps nor hinders: the group id and access id
        // still decide the match.
        Assert.True(withStrayScope.Matches(GroupRole("group-1", "member")));
        Assert.False(withStrayScope.Matches(GroupRole("group-2", "member")));
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

    // A group assignment carries no Scope: the group id is its scope, and the
    // access id ("member" / "owner") fills the role-definition slot.
    private static ActiveRoleAssignment GroupActive(
        string? groupId, string accessId, string roleName = "Member")
        => new(
            PimSource.EntraGroup,
            roleName,
            accessId,
            Scope: null,
            EndDateTime: null,
            GroupId: groupId);

    private static UnifiedEligibleRole GroupRole(
        string? groupId, string accessId, string roleName = "Member")
        => new(
            Source: PimSource.EntraGroup,
            RoleName: roleName,
            RoleDefinitionId: accessId,
            ScopeDisplay: "Contoso SQL Admins",
            ArmScope: null,
            EligibilityId: "elig-group-1",
            GroupId: groupId);

    private static UnifiedEligibleRole ArmRole(string roleName, string roleDefinitionId, string? armScope)
        => new(
            Source: PimSource.AzureRbac,
            RoleName: roleName,
            RoleDefinitionId: roleDefinitionId,
            ScopeDisplay: "Dev (sub)",
            ArmScope: armScope,
            EligibilityId: "elig-arm-1");
}
