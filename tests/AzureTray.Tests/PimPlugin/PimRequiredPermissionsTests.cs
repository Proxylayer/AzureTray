using System;
using System.Collections.Generic;
using System.Linq;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Permissions;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Every scope id the PIM plugin asks the host to consent to, pinned by value.
//
// This is not defensive testing for its own sake. The host writes each id
// straight into requiredResourceAccess as ResourceAccessDto(id, "Scope"), and
// Entra consents to whatever the id names — not to what the ScopeName string
// next to it says. An id copied from the appRoles list (the application
// permission of the same name) or from a neighbouring permission therefore
// mis-consents SILENTLY: the consent screen shows a different permission than
// the plugin's own text, and the runtime failure lands much later as a 403 that
// looks like a tenant configuration problem.
//
// Two of these were wrong in a shipped version and have just been corrected
// against the Graph service principal's oauth2PermissionScopes, so the exact
// values are recorded here rather than left to inspection:
//
//   az ad sp show --id 00000003-0000-0000-c000-000000000000 \
//     --query "oauth2PermissionScopes[?value=='<Scope.Name>'].id"
public sealed class PimRequiredPermissionsTests
{
    // The full expected list, in declaration order: scope name -> delegated
    // scope id. Ordering is asserted too, since it is what the consent screen
    // shows.
    private static readonly (PermissionApi Api, string ScopeName, string ScopeId)[] Expected =
    {
        (PermissionApi.MicrosoftGraph, "User.Read", "e1fe6dd8-ba31-4d61-89e7-88639da4683d"),
        (PermissionApi.MicrosoftGraph, "RoleAssignmentSchedule.ReadWrite.Directory", "8c026be3-8e26-4774-9372-8d5d6f21daff"),
        (PermissionApi.MicrosoftGraph, "RoleEligibilitySchedule.Read.Directory", "eb0788c2-6d4e-4658-8c9e-c0fb8053f03d"),
        (PermissionApi.MicrosoftGraph, "PrivilegedAccess.ReadWrite.AzureAD", "3c3c74f5-cdaa-4a97-b7e0-4e788bfcfb37"),
        (PermissionApi.MicrosoftGraph, "RoleManagement.Read.Directory", "741c54c3-0c1e-44a1-818b-3f97ab4e8c83"),
        (PermissionApi.MicrosoftGraph, "PrivilegedEligibilitySchedule.Read.AzureADGroup", "8f44f93d-ecef-46ae-a9bf-338508d44d6b"),
        (PermissionApi.MicrosoftGraph, "PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup", "06dbc45d-6708-4ef0-a797-f797ee68bf4b"),
        (PermissionApi.MicrosoftGraph, "RoleManagementPolicy.Read.AzureADGroup", "7e26fdff-9cb1-4e56-bede-211fe0e420e8"),
        (PermissionApi.AzureResourceManager, "user_impersonation", "41094075-9dad-400e-a0bd-54e686782033"),
    };

    [Fact]
    public void All_DeclaresExactlyTheExpectedScopes_WithTheirVerifiedIds()
    {
        var actual = PimRequiredPermissions.All
            .Select(p => (p.Api, p.ScopeName, p.ScopeId))
            .ToArray();

        Assert.Equal(Expected, actual);
    }

    // The three PIM for Groups scopes. The AzureADGroup suffix governs
    // identityGovernance/privilegedAccess/group, and no amount of
    // RoleManagement.*.Directory grants it — so these are additions, not
    // replacements, and each has to be present in its own right.
    [Theory]
    [InlineData("PrivilegedEligibilitySchedule.Read.AzureADGroup", "8f44f93d-ecef-46ae-a9bf-338508d44d6b")]
    [InlineData("PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup", "06dbc45d-6708-4ef0-a797-f797ee68bf4b")]
    [InlineData("RoleManagementPolicy.Read.AzureADGroup", "7e26fdff-9cb1-4e56-bede-211fe0e420e8")]
    public void All_DeclaresTheGroupScope_WithItsVerifiedId(string scopeName, string scopeId)
    {
        var permission = Assert.Single(PimRequiredPermissions.All, p => p.ScopeName == scopeName);

        Assert.Equal(PermissionApi.MicrosoftGraph, permission.Api);
        Assert.Equal(scopeId, permission.ScopeId, StringComparer.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(permission.DisplayName));
    }

    // The two ids that were wrong before: a regression here re-introduces a
    // silent mis-consent rather than an error anybody would notice.
    [Theory]
    [InlineData("RoleEligibilitySchedule.Read.Directory", "eb0788c2-6d4e-4658-8c9e-c0fb8053f03d")]
    [InlineData("RoleManagement.Read.Directory", "741c54c3-0c1e-44a1-818b-3f97ab4e8c83")]
    public void All_DeclaresTheCorrectedDirectoryScope_WithItsVerifiedId(string scopeName, string scopeId)
    {
        var permission = Assert.Single(PimRequiredPermissions.All, p => p.ScopeName == scopeName);

        Assert.Equal(scopeId, permission.ScopeId, StringComparer.Ordinal);
    }

    // A duplicated id is the exact shape a copy-paste mistake takes, and it
    // consents the user to one permission twice while omitting another.
    [Fact]
    public void All_ScopeIdsAreUnique()
    {
        var ids = PimRequiredPermissions.All.Select(p => p.ScopeId).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void All_ScopeNamesAreUnique()
    {
        var names = PimRequiredPermissions.All.Select(p => p.ScopeName).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // Every id must be a GUID: the host emits it verbatim into the app
    // registration manifest, where a non-GUID is rejected outright.
    [Fact]
    public void All_ScopeIdsAreGuids()
    {
        Assert.All(PimRequiredPermissions.All, p => Assert.True(
            Guid.TryParse(p.ScopeId, out _),
            $"{p.ScopeName} has a non-GUID scope id: {p.ScopeId}"));
    }

    // The plugin surfaces the same list it declares — the menu's consent
    // prompt and the manifest writer both read it through the plugin.
    [Fact]
    public void PluginRequiredPermissions_MatchTheDeclaredList()
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();

        Assert.Equal(
            PimRequiredPermissions.All.Select(p => p.ScopeId),
            plugin.RequiredPermissions.Select(p => p.ScopeId));
    }

    // Only the ARM scope may target Azure Resource Manager; a group scope
    // routed there would be consented against the wrong resource entirely.
    [Fact]
    public void All_OnlyUserImpersonationTargetsAzureResourceManager()
    {
        var arm = Assert.Single(
            PimRequiredPermissions.All, p => p.Api == PermissionApi.AzureResourceManager);

        Assert.Equal("user_impersonation", arm.ScopeName, StringComparer.Ordinal);
    }
}
