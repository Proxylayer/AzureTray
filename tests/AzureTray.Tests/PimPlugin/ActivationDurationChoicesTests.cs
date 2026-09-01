using System;
using System.Linq;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Clamping of the activation-duration prompt to the role's policy maximum.
// Offering a duration above the cap earns a server-side 400 that surfaces as a
// generic activation failure, so "never above the cap" is the load-bearing
// invariant here; "never empty" is its counterweight (a tight cap must still
// leave the user something to pick).
public sealed class ActivationDurationChoicesTests
{
    // ---- worked examples --------------------------------------------------

    [Fact]
    public void Build_CapOfTwoHours_OffersTheOneHourStepPlusTheCap()
    {
        Assert.Equal(new[] { "1 hour", "2 hours" }, Labels(TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Build_CapOfEightHours_OffersTheThreeStandardSteps()
    {
        Assert.Equal(new[] { "1 hour", "4 hours", "8 hours" }, Labels(TimeSpan.FromHours(8)));
    }

    [Fact]
    public void Build_CapOfSevenHours_OffersTheStepsBelowItPlusTheCap()
    {
        Assert.Equal(new[] { "1 hour", "4 hours", "7 hours" }, Labels(TimeSpan.FromHours(7)));
    }

    // Tighter than the smallest standard step: the cap alone, never an empty
    // prompt and never the 1h step the policy would reject.
    [Fact]
    public void Build_CapBelowTheSmallestStep_OffersTheCapAlone()
    {
        Assert.Equal(new[] { "30 min" }, Labels(TimeSpan.FromMinutes(30)));
    }

    // A cap that already is one of the steps must not be appended a second time.
    [Theory]
    [InlineData(60, new[] { "1 hour" })]
    [InlineData(240, new[] { "1 hour", "4 hours" })]
    [InlineData(480, new[] { "1 hour", "4 hours", "8 hours" })]
    public void Build_CapEqualToAStep_DoesNotDuplicateIt(int capMinutes, string[] expected)
    {
        Assert.Equal(expected, Labels(TimeSpan.FromMinutes(capMinutes)));
    }

    // An unknown cap keeps the pre-policy-read behaviour: the full standard list.
    // Which cap "unknown" resolves to is provider-specific — see EffectiveCap.
    [Fact]
    public void Build_UnknownCap_OffersTheFullStandardList()
    {
        Assert.Equal(new[] { "1 hour", "4 hours", "8 hours" }, Labels(cap: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Build_NonPositiveCap_IsTreatedAsUnknown(int capMinutes)
    {
        Assert.Equal(new[] { "1 hour", "4 hours", "8 hours" }, Labels(TimeSpan.FromMinutes(capMinutes)));
    }

    // ---- label formatting -------------------------------------------------

    [Theory]
    [InlineData(60, "1 hour")]          // whole hour, singular
    [InlineData(4 * 60, "4 hours")]     // whole hours, plural
    [InlineData(23 * 60, "23 hours")]   // still under a day
    [InlineData(30, "30 min")]          // sub-hour falls back to FormatDuration
    [InlineData(90, "1h 30m")]          // non-whole hour falls back
    [InlineData(24 * 60, "1d")]         // a day or more falls back
    [InlineData(48 * 60, "2d")]
    public void Build_LabelsTheCap_PerTheWholeHourRule(int capMinutes, string expectedLabel)
    {
        var cap = TimeSpan.FromMinutes(capMinutes);

        var choice = Assert.Single(ActivationDurationChoices.Build(cap), c => c.Duration == cap);

        Assert.Equal(expectedLabel, choice.Label);
    }

    // The non-whole-hour and multi-day labels are exactly what the shared
    // formatter produces — no second formatting dialect to drift out of sync.
    [Theory]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(24 * 60)]
    [InlineData(48 * 60)]
    public void Build_FallbackLabels_MatchFormatDuration(int capMinutes)
    {
        var cap = TimeSpan.FromMinutes(capMinutes);

        var choice = Assert.Single(ActivationDurationChoices.Build(cap), c => c.Duration == cap);

        Assert.Equal(EligibleRolesWatcher.FormatDuration(cap), choice.Label);
    }

    // ---- invariants across a spread of caps -------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(90)]
    [InlineData(239)]
    [InlineData(240)]
    [InlineData(300)]
    [InlineData(420)]
    [InlineData(479)]
    [InlineData(480)]
    [InlineData(481)]
    [InlineData(540)]
    [InlineData(12 * 60)]
    [InlineData(24 * 60)]
    [InlineData(48 * 60)]
    [InlineData(365 * 24 * 60)]
    public void Build_IsNeverEmpty_NeverAboveTheCap_AndStrictlyAscending(int capMinutes)
    {
        var cap = TimeSpan.FromMinutes(capMinutes);

        var choices = ActivationDurationChoices.Build(cap);

        Assert.NotEmpty(choices);
        Assert.All(choices, c => Assert.True(
            c.Duration <= cap,
            $"'{c.Label}' ({c.Duration}) is above the cap {cap}."));
        Assert.All(choices, c => Assert.True(
            c.Duration > TimeSpan.Zero,
            $"'{c.Label}' is not a positive duration."));
        for (var i = 1; i < choices.Count; i++)
        {
            Assert.True(
                choices[i].Duration > choices[i - 1].Duration,
                $"choices must ascend strictly: {choices[i - 1].Label} then {choices[i].Label}.");
        }
        Assert.Equal(choices.Count, choices.Select(c => c.Label).Distinct(StringComparer.Ordinal).Count());
    }

    // ---- Match ------------------------------------------------------------

    // Every label a generated list offers resolves back to its own duration —
    // the property the notifier round-trip depends on.
    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(420)]
    [InlineData(480)]
    [InlineData(48 * 60)]
    public void Match_EveryOfferedLabel_ResolvesToItsOwnDuration(int capMinutes)
    {
        var choices = ActivationDurationChoices.Build(TimeSpan.FromMinutes(capMinutes));

        foreach (var choice in choices)
        {
            Assert.Equal(choice.Duration, ActivationDurationChoices.Match(choices, choice.Label));
        }
    }

    [Fact]
    public void Match_ResolvesTheCapLabel_WithoutReparsingTheDisplayString()
    {
        var choices = ActivationDurationChoices.Build(TimeSpan.FromHours(7));

        Assert.Equal(TimeSpan.FromHours(7), ActivationDurationChoices.Match(choices, "7 hours"));
        Assert.Equal(TimeSpan.FromHours(1), ActivationDurationChoices.Match(choices, "1 hour"));
    }

    // A label that is not in the list it was matched against resolves to null —
    // never to a neighbouring duration. "8 hours" is a real label from the
    // unclamped list, so this is the stale-prompt case: the caller must abandon
    // the activation rather than silently send a duration the policy rejects.
    [Theory]
    [InlineData("8 hours")]
    [InlineData("3 hours")]
    [InlineData("30 min")]
    [InlineData("2h")]
    [InlineData("1 Hour")]   // Ordinal comparison: casing must match exactly.
    [InlineData("1 hour ")]  // and so must surrounding whitespace.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Match_LabelNotInTheList_ResolvesToNull(string? label)
    {
        var choices = ActivationDurationChoices.Build(TimeSpan.FromHours(2));

        Assert.Null(ActivationDurationChoices.Match(choices, label));
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
    // prompt — the ceiling is the same number either way.
    [Fact]
    public void For_EntraWithUnknownCap_OffersTheSameListAsAnEightHourPolicy()
    {
        var fallback = ActivationDurationChoices.For(Role(PimSource.EntraId, cap: null));
        var explicitEightHours = ActivationDurationChoices.For(Role(PimSource.EntraId, TimeSpan.FromHours(8)));

        Assert.Equal(
            explicitEightHours.Select(c => c.Label),
            fallback.Select(c => c.Label));
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
    public void For_EntraGroupWithUnknownCap_OffersTheSameListAsAnEightHourPolicy()
    {
        var fallback = ActivationDurationChoices.For(GroupRole(cap: null));

        Assert.Equal(new[] { "1 hour", "4 hours", "8 hours" }, fallback.Select(c => c.Label));
    }

    [Fact]
    public void For_ArmWithATightCap_ClampsThePrompt()
    {
        var choices = ActivationDurationChoices.For(Role(PimSource.AzureRbac, TimeSpan.FromHours(2)));

        Assert.Equal(new[] { "1 hour", "2 hours" }, choices.Select(c => c.Label));
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

    // A cap at or above the longest standard step does not restrict anything,
    // so it stays quiet too.
    [Theory]
    [InlineData(480)]
    [InlineData(481)]
    [InlineData(12 * 60)]
    [InlineData(48 * 60)]
    public void CapHint_CapNotTighterThanTheLongestStep_IsSilent(int capMinutes)
    {
        Assert.Null(ActivationDurationChoices.CapHint(
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

    private static string[] Labels(TimeSpan? cap)
        => ActivationDurationChoices.Build(cap).Select(c => c.Label).ToArray();

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
