using System;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// MatchesRole re-anchors a tracked approval to the eligible-role row it was
// submitted from, so the menu can grey that row "(pending)". Identity is
// source + role definition + the scope the eligibility applies at.
public sealed class PendingActivationRequestMatchTests
{
    [Fact]
    public void Entra_SameRoleAndScope_Matches()
    {
        var request = EntraRequest(roleDefinitionId: "role-owner", directoryScopeId: "/");

        Assert.True(request.MatchesRole(EntraRole(roleDefinitionId: "role-owner", directoryScopeId: "/")));
    }

    // Entries persisted by builds before RoleDefinitionId existed carry null —
    // they must never grey a row.
    [Fact]
    public void LegacyEntry_NullRoleDefinitionId_NeverMatches()
    {
        var request = EntraRequest(roleDefinitionId: null, directoryScopeId: "/");

        Assert.False(request.MatchesRole(EntraRole(roleDefinitionId: "role-owner", directoryScopeId: "/")));
    }

    [Fact]
    public void SourceMismatch_DoesNotMatch()
    {
        var request = EntraRequest(roleDefinitionId: "role-owner", directoryScopeId: "/");

        Assert.False(request.MatchesRole(ArmRole(roleDefinitionId: "role-owner", armScope: "/subscriptions/sub-1")));
    }

    [Fact]
    public void RoleDefinitionMismatch_DoesNotMatch()
    {
        var request = EntraRequest(roleDefinitionId: "role-owner", directoryScopeId: "/");

        Assert.False(request.MatchesRole(EntraRole(roleDefinitionId: "role-reader", directoryScopeId: "/")));
    }

    // Entra ID: "/", null and absent all mean the whole directory, so a null
    // persisted scope matches a "/" row and vice versa.
    [Theory]
    [InlineData(null, "/")]
    [InlineData("/", null)]
    [InlineData(null, null)]
    [InlineData("/", "/")]
    public void Entra_NullAndSlashDirectoryScopes_AreEquivalent(string? requestScope, string? roleScope)
    {
        var request = EntraRequest(roleDefinitionId: "role-owner", directoryScopeId: requestScope);

        Assert.True(request.MatchesRole(EntraRole(roleDefinitionId: "role-owner", directoryScopeId: roleScope)));
    }

    [Fact]
    public void Entra_AdministrativeUnitScope_DoesNotMatchDirectoryWideRow()
    {
        var request = EntraRequest(roleDefinitionId: "role-owner", directoryScopeId: "/administrativeUnits/au-1");

        Assert.False(request.MatchesRole(EntraRole(roleDefinitionId: "role-owner", directoryScopeId: "/")));
    }

    [Fact]
    public void Arm_SameRoleAndScope_Matches()
    {
        var request = ArmRequest(roleDefinitionId: "arm-role-contrib", armScope: "/subscriptions/sub-1");

        Assert.True(request.MatchesRole(ArmRole(roleDefinitionId: "arm-role-contrib", armScope: "/subscriptions/sub-1")));
    }

    [Fact]
    public void Arm_ScopeMismatch_DoesNotMatch()
    {
        var request = ArmRequest(roleDefinitionId: "arm-role-contrib", armScope: "/subscriptions/sub-1");

        Assert.False(request.MatchesRole(ArmRole(roleDefinitionId: "arm-role-contrib", armScope: "/subscriptions/sub-2")));
    }

    [Fact]
    public void Group_SameAccessAndGroup_Matches()
    {
        var request = GroupRequest(roleDefinitionId: "member", groupId: "group-1");

        Assert.True(request.MatchesRole(GroupRole(roleDefinitionId: "member", groupId: "group-1")));
    }

    [Fact]
    public void Group_GroupIdMismatch_DoesNotMatch()
    {
        var request = GroupRequest(roleDefinitionId: "member", groupId: "group-1");

        Assert.False(request.MatchesRole(GroupRole(roleDefinitionId: "member", groupId: "group-2")));
    }

    // ---- builders ---------------------------------------------------------

    private static PendingActivationRequest EntraRequest(string? roleDefinitionId, string? directoryScopeId)
        => new(
            Source: PimSource.EntraId,
            RequestId: "req-1",
            RoleName: "Owner",
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            SubmittedAt: DateTimeOffset.UtcNow,
            RoleDefinitionId: roleDefinitionId,
            DirectoryScopeId: directoryScopeId);

    private static PendingActivationRequest ArmRequest(string roleDefinitionId, string armScope)
        => new(
            Source: PimSource.AzureRbac,
            RequestId: "req-arm-1",
            RoleName: "Contributor",
            ScopeDisplay: "Dev (sub)",
            ArmScope: armScope,
            SubmittedAt: DateTimeOffset.UtcNow,
            RoleDefinitionId: roleDefinitionId);

    private static PendingActivationRequest GroupRequest(string roleDefinitionId, string groupId)
        => new(
            Source: PimSource.EntraGroup,
            RequestId: "req-group-1",
            RoleName: "Member",
            ScopeDisplay: "Contoso SQL Admins",
            ArmScope: null,
            SubmittedAt: DateTimeOffset.UtcNow,
            RoleDefinitionId: roleDefinitionId,
            GroupId: groupId);

    private static UnifiedEligibleRole EntraRole(string roleDefinitionId, string? directoryScopeId)
        => new(
            Source: PimSource.EntraId,
            RoleName: "Owner",
            RoleDefinitionId: roleDefinitionId,
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            EligibilityId: "elig-1",
            DirectoryScopeId: directoryScopeId);

    private static UnifiedEligibleRole ArmRole(string roleDefinitionId, string armScope)
        => new(
            Source: PimSource.AzureRbac,
            RoleName: "Contributor",
            RoleDefinitionId: roleDefinitionId,
            ScopeDisplay: "Dev (sub)",
            ArmScope: armScope,
            EligibilityId: "elig-arm-1");

    private static UnifiedEligibleRole GroupRole(string roleDefinitionId, string groupId)
        => new(
            Source: PimSource.EntraGroup,
            RoleName: "Member",
            RoleDefinitionId: roleDefinitionId,
            ScopeDisplay: "Contoso SQL Admins",
            ArmScope: null,
            EligibilityId: "elig-group-1",
            GroupId: groupId);
}
