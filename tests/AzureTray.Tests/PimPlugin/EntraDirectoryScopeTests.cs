using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The three readings of Graph's directoryScopeId: the group key (which scopes
// are the same scope), the menu label, and the value sent on an activation.
// Almost every eligibility is directory-wide, so the "/" folding is the path
// that matters most — but an administrative-unit-scoped eligibility must stay
// distinguishable from it in all three, or the row is mislabelled and the
// activation asks for a grant the user is not eligible for.
public sealed class EntraDirectoryScopeTests
{
    private const string DirectoryDisplay = "Entra ID directory";
    private const string AuScope = "/administrativeUnits/au-1";

    // ---- NormalizeForKey --------------------------------------------------

    // An absent scope and an explicit "/" are the same scope: a response that
    // omitted the member, a cache row written before the plugin persisted it,
    // and a directory-wide grant must all land in one group.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("  /  ")]
    public void NormalizeForKey_FoldsAbsentAndDirectoryWideScopesToTheDirectory(string? scope)
    {
        Assert.Equal("/", EntraDirectoryScope.NormalizeForKey(scope));
    }

    [Fact]
    public void NormalizeForKey_StripsATrailingSlash()
    {
        Assert.Equal(
            EntraDirectoryScope.NormalizeForKey(AuScope),
            EntraDirectoryScope.NormalizeForKey(AuScope + "/"));
    }

    // Graph's casing on object ids is not guaranteed stable across responses.
    [Fact]
    public void NormalizeForKey_FoldsCasing()
    {
        Assert.Equal(
            EntraDirectoryScope.NormalizeForKey(AuScope),
            EntraDirectoryScope.NormalizeForKey("/AdministrativeUnits/AU-1"));
    }

    [Fact]
    public void NormalizeForKey_TrimsSurroundingWhitespace()
    {
        Assert.Equal(
            EntraDirectoryScope.NormalizeForKey(AuScope),
            EntraDirectoryScope.NormalizeForKey($"  {AuScope}  "));
    }

    // The over-collapse guard: two different administrative units, and an
    // administrative unit versus the directory, are different keys.
    [Fact]
    public void NormalizeForKey_KeepsGenuinelyDifferentScopesApart()
    {
        Assert.NotEqual(
            EntraDirectoryScope.NormalizeForKey(AuScope),
            EntraDirectoryScope.NormalizeForKey("/administrativeUnits/au-2"));
        Assert.NotEqual(
            EntraDirectoryScope.NormalizeForKey(AuScope),
            EntraDirectoryScope.NormalizeForKey("/"));
    }

    // ---- DisplayFor -------------------------------------------------------

    // Pinned string: it is the row text the menu renders and the cap-row tests
    // assert against verbatim.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void DisplayFor_DirectoryWideScope_IsTheExactDirectoryLabel(string? scope)
    {
        Assert.Equal(DirectoryDisplay, EntraDirectoryScope.DisplayFor(scope));
    }

    [Fact]
    public void DisplayFor_AdministrativeUnitScope_NamesTheUnitById()
    {
        Assert.Equal("Administrative unit au-1", EntraDirectoryScope.DisplayFor(AuScope));
    }

    // The prefix match is case-insensitive; the id keeps its own casing because
    // that is what Graph returned.
    [Fact]
    public void DisplayFor_AdministrativeUnitScope_MatchesThePrefixCaseInsensitively()
    {
        Assert.Equal(
            "Administrative unit AU-1",
            EntraDirectoryScope.DisplayFor("/AdministrativeUnits/AU-1"));
    }

    // Resolving a scope's display name would cost a Graph call per scope and the
    // eligibility response does not carry it, so anything else shows its raw id.
    [Fact]
    public void DisplayFor_AnyOtherScope_ShowsTheRawScopeId()
    {
        Assert.Equal(
            "/applications/app-1",
            EntraDirectoryScope.DisplayFor("/applications/app-1"));
        Assert.Equal(
            "/applications/app-1",
            EntraDirectoryScope.DisplayFor("  /applications/app-1  "));
    }

    // ---- OrDirectory ------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    public void OrDirectory_AbsentScope_FallsBackToTheDirectory(string? scope)
    {
        Assert.Equal("/", EntraDirectoryScope.OrDirectory(scope));
    }

    // Passed through as Graph gave it — casing included, since this value goes
    // back onto the wire as directoryScopeId.
    [Fact]
    public void OrDirectory_RealScope_IsPassedThroughTrimmedAndCasePreserved()
    {
        Assert.Equal("/administrativeUnits/AU-1", EntraDirectoryScope.OrDirectory("/administrativeUnits/AU-1"));
        Assert.Equal(AuScope, EntraDirectoryScope.OrDirectory($"  {AuScope}  "));
    }
}
