using Xunit;
using AzureTray.Plugin.PIM.Watchers;

namespace AzureTray.Tests.PimPlugin;

// EligibleRolesWatcher.ExtractManagementGroupScope pulls the MG scope prefix
// out of an ARM scope string, normalized with a leading slash and canonical
// provider-path casing. Non-MG scopes (subscriptions, resource groups) and
// malformed inputs must yield null so they never enter the approver fan-out.
public sealed class EligibleRolesWatcherScopeExtractionTests
{
    private const string CanonicalMg = "/providers/Microsoft.Management/managementGroups/mg-1";

    [Theory]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg-1")]
    // Missing leading slash is normalized back on.
    [InlineData("providers/Microsoft.Management/managementGroups/mg-1")]
    // Provider-path casing is matched case-insensitively and canonicalized.
    [InlineData("/PROVIDERS/microsoft.management/MANAGEMENTGROUPS/mg-1")]
    // Trailing segments are tolerated (MG scopes have none in practice).
    [InlineData("/providers/Microsoft.Management/managementGroups/mg-1/extra/segment")]
    public void ExtractManagementGroupScope_MgScope_ReturnsNormalizedScope(string armScope)
    {
        Assert.Equal(CanonicalMg, EligibleRolesWatcher.ExtractManagementGroupScope(armScope));
    }

    [Fact]
    public void ExtractManagementGroupScope_PreservesManagementGroupIdCasing()
    {
        var result = EligibleRolesWatcher.ExtractManagementGroupScope(
            "/providers/Microsoft.Management/managementGroups/Contoso-Root");
        Assert.Equal("/providers/Microsoft.Management/managementGroups/Contoso-Root", result);
    }

    [Theory]
    // Subscription and descendant scopes are not management groups.
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001")]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-1")]
    // Other providers must not match.
    [InlineData("/providers/Microsoft.Authorization/roleAssignments/ra-1")]
    // MG prefix with no id yields nothing to query.
    [InlineData("/providers/Microsoft.Management/managementGroups/")]
    [InlineData("/providers/Microsoft.Management/managementGroups")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractManagementGroupScope_NonMgOrEmptyScope_ReturnsNull(string? armScope)
    {
        Assert.Null(EligibleRolesWatcher.ExtractManagementGroupScope(armScope));
    }
}
