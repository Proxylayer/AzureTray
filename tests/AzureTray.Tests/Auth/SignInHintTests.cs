using AzureTray.Auth;
using Xunit;

namespace AzureTray.Tests.Auth;

public sealed class SignInHintTests
{
    [Theory]
    [InlineData("jbland_proxylayer.net#EXT#@cfoadminpgcfo.onmicrosoft.com", true)]
    [InlineData("jbland_proxylayer.net#ext#@cfoadminpgcfo.onmicrosoft.com", true)]
    [InlineData("jbland@proxylayer.net", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsExternal_DetectsGuestUpn(string? value, bool expected)
        => Assert.Equal(expected, SignInHint.IsExternal(value));

    [Fact]
    public void Pick_PrefersCleanEmailOverExternalUpn()
    {
        var result = SignInHint.Pick(
            "jbland@proxylayer.net",
            "jbland_proxylayer.net#EXT#@cfoadminpgcfo.onmicrosoft.com");

        Assert.Equal("jbland@proxylayer.net", result);
    }

    [Fact]
    public void Pick_SkipsBlankCandidates()
    {
        var result = SignInHint.Pick(null, "   ", "alice@contoso.com");
        Assert.Equal("alice@contoso.com", result);
    }

    [Fact]
    public void Pick_SkipsExternalEvenWhenItComesFirst()
    {
        var result = SignInHint.Pick(
            "jbland_proxylayer.net#EXT#@cfoadminpgcfo.onmicrosoft.com",
            "jbland@proxylayer.net");

        Assert.Equal("jbland@proxylayer.net", result);
    }

    [Fact]
    public void Pick_UnmanglesWhenOnlyExternalAvailable()
    {
        var result = SignInHint.Pick(
            null,
            "jbland_proxylayer.net#EXT#@cfoadminpgcfo.onmicrosoft.com");

        Assert.Equal("jbland@proxylayer.net", result);
    }

    [Fact]
    public void Pick_TrimsWhitespace()
        => Assert.Equal("alice@contoso.com", SignInHint.Pick("  alice@contoso.com  "));

    [Fact]
    public void Pick_ReturnsNullWhenNothingUsable()
        => Assert.Null(SignInHint.Pick(null, "", "   "));

    [Theory]
    [InlineData("jbland_proxylayer.net#EXT#@cfoadminpgcfo.onmicrosoft.com", "jbland@proxylayer.net")]
    [InlineData("alice_contoso.com#EXT#@fabrikam.onmicrosoft.com", "alice@contoso.com")]
    public void UnmangleExternalUpn_RebuildsHomeEmail(string upn, string expected)
        => Assert.Equal(expected, SignInHint.UnmangleExternalUpn(upn));

    [Theory]
    [InlineData("jbland@proxylayer.net")]   // not an #EXT# form
    [InlineData("#EXT#@tenant.onmicrosoft.com")]  // nothing before the marker
    [InlineData(null)]
    public void UnmangleExternalUpn_ReturnsNullForNonGuestInput(string? upn)
        => Assert.Null(SignInHint.UnmangleExternalUpn(upn));
}
