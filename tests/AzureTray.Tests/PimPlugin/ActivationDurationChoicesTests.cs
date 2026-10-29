using System;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Clamping of the activation-duration slider to the role's policy maximum.
// Offering a duration above the cap earns a server-side 400 that surfaces as a
// generic activation failure, so "never above the cap" is the load-bearing
// invariant here; "never empty" is its counterweight (a tight cap must still
// leave the user something to pick).
public sealed class ActivationDurationChoicesTests
{
    // ---- For (slider geometry) ---------------------------------------------

    [Fact]
    public void For_KnownCap_SlidesFromThirtyMinutesToTheCapInThirtyMinuteSteps()
    {
        var slider = ActivationDurationChoices.For(Role(PimSource.EntraId, TimeSpan.FromHours(4)));

        Assert.Equal(TimeSpan.FromMinutes(30), slider.Minimum);
        Assert.Equal(TimeSpan.FromHours(4), slider.Maximum);
        Assert.Equal(TimeSpan.FromMinutes(30), slider.Step);
    }

    // The thumb starts at the long-standing 1-hour default.
    [Fact]
    public void For_CapAboveAnHour_StartsTheThumbAtOneHour()
    {
        var slider = ActivationDurationChoices.For(Role(PimSource.EntraId, TimeSpan.FromHours(8)));

        Assert.Equal(TimeSpan.FromHours(1), slider.Initial);
    }

    // A cap under the default initial pins the thumb to the cap — the slider
    // must never start on a value the policy rejects.
    [Fact]
    public void For_CapBelowAnHour_StartsTheThumbAtTheCap()
    {
        var slider = ActivationDurationChoices.For(Role(PimSource.EntraId, TimeSpan.FromMinutes(30)));

        Assert.Equal(TimeSpan.FromMinutes(30), slider.Initial);
        Assert.Equal(TimeSpan.FromMinutes(30), slider.Maximum);
    }

    // A cap tighter than one step collapses the slider to that single stop
    // rather than ever offering more than the policy allows.
    [Fact]
    public void For_CapTighterThanOneStep_CollapsesToTheCapAlone()
    {
        var slider = ActivationDurationChoices.For(Role(PimSource.EntraId, TimeSpan.FromMinutes(15)));

        Assert.Equal(TimeSpan.FromMinutes(15), slider.Minimum);
        Assert.Equal(TimeSpan.FromMinutes(15), slider.Maximum);
        Assert.Equal(TimeSpan.FromMinutes(15), slider.Initial);
    }

    // An unknown cap keeps the pre-policy-read ceiling: 8 hours, the longest
    // duration the old fixed choice list ever offered.
    [Fact]
    public void For_UnknownCap_SlidesUpToEightHours()
    {
        var entra = ActivationDurationChoices.For(Role(PimSource.EntraId, cap: null));
        var arm = ActivationDurationChoices.For(Role(PimSource.AzureRbac, cap: null));

        Assert.Equal(TimeSpan.FromHours(8), entra.Maximum);
        Assert.Equal(TimeSpan.FromHours(8), arm.Maximum);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void For_NonPositiveCap_IsTreatedAsUnknown(int capMinutes)
    {
        var slider = ActivationDurationChoices.For(
            Role(PimSource.AzureRbac, TimeSpan.FromMinutes(capMinutes)));

        Assert.Equal(TimeSpan.FromHours(8), slider.Maximum);
        Assert.Equal(TimeSpan.FromMinutes(30), slider.Minimum);
    }

    // ARM caps may legitimately exceed the Graph ceiling; the slider follows.
    [Fact]
    public void For_ArmCapAboveEightHours_SlidesUpToTheCap()
    {
        var slider = ActivationDurationChoices.For(Role(PimSource.AzureRbac, TimeSpan.FromHours(24)));

        Assert.Equal(TimeSpan.FromHours(24), slider.Maximum);
    }

    // Invariants across a spread of caps: a usable, in-range slider whatever
    // the policy says.
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(240)]
    [InlineData(479)]
    [InlineData(480)]
    [InlineData(481)]
    [InlineData(12 * 60)]
    [InlineData(24 * 60)]
    [InlineData(48 * 60)]
    [InlineData(365 * 24 * 60)]
    public void For_AnyPositiveCap_YieldsAnOrderedInRangeSlider(int capMinutes)
    {
        var cap = TimeSpan.FromMinutes(capMinutes);

        var slider = ActivationDurationChoices.For(Role(PimSource.AzureRbac, cap));

        Assert.True(slider.Minimum > TimeSpan.Zero);
        Assert.True(slider.Minimum <= slider.Maximum);
        Assert.True(slider.Maximum <= cap, $"maximum {slider.Maximum} is above the cap {cap}");
        Assert.InRange(slider.Initial, slider.Minimum, slider.Maximum);
        Assert.Equal(TimeSpan.FromMinutes(30), slider.Step);
    }

    // ---- EffectiveCap -----------------------------------------------------

    // Entra ID caps role activation at 8 hours in the service itself, so an
    // unreadable Entra policy still has a documented ceiling to clamp to.
    [Fact]
    public void EffectiveCap_EntraWithUnknownCap_FallsBackToTheDocumentedEightHourCeiling()
    {
        Assert.Equal(
            TimeSpan.FromHours(8),
            ActivationDurationChoices.EffectiveCap(Role(PimSource.EntraId, cap: null)));
    }

    // Azure RBAC has no documented equivalent ceiling, so an unreadable ARM
    // policy means genuinely no cap — inventing 8h here would hide legitimate
    // longer activations.
    [Fact]
    public void EffectiveCap_ArmWithUnknownCap_HasNoCeiling()
    {
        Assert.Null(ActivationDurationChoices.EffectiveCap(Role(PimSource.AzureRbac, cap: null)));
    }

    [Fact]
    public void EffectiveCap_AKnownPolicyValueWins_OverTheEntraCeiling()
    {
        Assert.Equal(
            TimeSpan.FromHours(2),
            ActivationDurationChoices.EffectiveCap(Role(PimSource.EntraId, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void EffectiveCap_ArmMayExceedEightHours()
    {
        Assert.Equal(
            TimeSpan.FromHours(48),
            ActivationDurationChoices.EffectiveCap(Role(PimSource.AzureRbac, TimeSpan.FromHours(48))));
    }

    // The Entra fallback and the "cap read as 8h" case must offer the same
    // slider — the ceiling is the same number either way.
    [Fact]
    public void For_EntraWithUnknownCap_OffersTheSameSliderAsAnEightHourPolicy()
    {
        var fallback = ActivationDurationChoices.For(Role(PimSource.EntraId, cap: null));
        var explicitEightHours = ActivationDurationChoices.For(Role(PimSource.EntraId, TimeSpan.FromHours(8)));

        Assert.Equal(explicitEightHours, fallback);
    }

    // PIM for Groups is Graph-served and time-bound by the service to the same
    // 8 hours, so an unreadable group policy clamps to the documented ceiling
    // exactly as a directory role does — not to the unbounded ARM behaviour.
    [Fact]
    public void EffectiveCap_EntraGroupWithUnknownCap_FallsBackToTheDocumentedEightHourCeiling()
    {
        Assert.Equal(
            TimeSpan.FromHours(8),
            ActivationDurationChoices.EffectiveCap(GroupRole(cap: null)));
    }

    [Fact]
    public void EffectiveCap_EntraGroupWithAKnownPolicyValue_UsesIt()
    {
        Assert.Equal(
            TimeSpan.FromHours(2),
            ActivationDurationChoices.EffectiveCap(GroupRole(TimeSpan.FromHours(2))));
    }

    [Fact]
    public void For_EntraGroupWithUnknownCap_SlidesUpToEightHours()
    {
        var slider = ActivationDurationChoices.For(GroupRole(cap: null));

        Assert.Equal(TimeSpan.FromHours(8), slider.Maximum);
    }

    [Fact]
    public void For_ArmWithATightCap_ClampsTheSlider()
    {
        var slider = ActivationDurationChoices.For(Role(PimSource.AzureRbac, TimeSpan.FromHours(2)));

        Assert.Equal(TimeSpan.FromHours(2), slider.Maximum);
    }

    // ---- CapHint ----------------------------------------------------------

    // Nothing to say when the cap was never read: a policy-read failure is not
    // news the user can act on, and printing the 8h Entra fallback as if it were
    // the role's own policy would be a lie. (PimSource is internal, so these are
    // two Facts rather than one Theory — an InlineData of it would not compile.)
    [Fact]
    public void CapHint_EntraWithUnknownCap_IsSilent()
    {
        Assert.Null(ActivationDurationChoices.CapHint(Role(PimSource.EntraId, cap: null)));
    }

    [Fact]
    public void CapHint_ArmWithUnknownCap_IsSilent()
    {
        Assert.Null(ActivationDurationChoices.CapHint(Role(PimSource.AzureRbac, cap: null)));
    }

    // A cap at or above the longest standard step restricts nothing, but it is
    // still shown: users read a quiet row next to "(max 4h)" siblings as
    // missing data rather than as "no restriction".
    [Theory]
    [InlineData(480, "8h")]
    [InlineData(481, "8h 1m")]
    [InlineData(12 * 60, "12h")]
    [InlineData(48 * 60, "2d")]
    public void CapHint_CapNotTighterThanTheLongestStep_StillRenders(int capMinutes, string expected)
    {
        Assert.Equal(expected, ActivationDurationChoices.CapHint(
            Role(PimSource.AzureRbac, TimeSpan.FromMinutes(capMinutes))));
    }

    [Theory]
    [InlineData(120, "2h")]
    [InlineData(420, "7h")]
    [InlineData(30, "30 min")]
    [InlineData(90, "1h 30m")]
    [InlineData(479, "7h 59m")]
    public void CapHint_TighterCap_RendersTheCompactDuration(int capMinutes, string expected)
    {
        Assert.Equal(expected, ActivationDurationChoices.CapHint(
            Role(PimSource.EntraId, TimeSpan.FromMinutes(capMinutes))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void CapHint_NonPositiveCap_IsSilent(int capMinutes)
    {
        Assert.Null(ActivationDurationChoices.CapHint(
            Role(PimSource.EntraId, TimeSpan.FromMinutes(capMinutes))));
    }

    // ---- helpers ----------------------------------------------------------

    // A PIM for Groups row: the access id fills the role-definition slot and
    // the group is the scope, so neither ArmScope nor DirectoryScopeId is set.
    private static UnifiedEligibleRole GroupRole(TimeSpan? cap)
        => new(
            Source: PimSource.EntraGroup,
            RoleName: "Member",
            RoleDefinitionId: "member",
            ScopeDisplay: "Contoso SQL Admins",
            ArmScope: null,
            EligibilityId: "elig-group-1",
            MaxActivationDuration: cap,
            GroupId: "group-1");

    private static UnifiedEligibleRole Role(PimSource source, TimeSpan? cap)
        => new(
            Source: source,
            RoleName: source == PimSource.EntraId ? "Owner" : "Reader",
            RoleDefinitionId: source == PimSource.EntraId ? "role-owner" : "role-reader",
            ScopeDisplay: source == PimSource.EntraId ? "Entra ID directory" : "Dev sub",
            ArmScope: source == PimSource.EntraId ? null : "/subscriptions/sub-1",
            EligibilityId: "elig-1",
            MaxActivationDuration: cap);
}
