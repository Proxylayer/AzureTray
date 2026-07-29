using System;
using System.Collections.Generic;
using System.Linq;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The collapse of duplicate eligibility rows. Two risks pull in opposite
// directions and both are pinned here: under-collapsing leaves the ARM fan-out's
// duplicate rows in the menu (one management-group eligibility once per
// subscription beneath it), while over-collapsing hides a role the user really
// does hold separately — a different scope, a different administrative unit, or
// the same display name on the other provider.
public sealed class EligibleRoleDeduplicatorTests
{
    private const string SubScope = "/subscriptions/sub-1";
    private const string OtherSubScope = "/subscriptions/sub-2";
    private const string MgScope = "/providers/Microsoft.Management/managementGroups/mg-1";
    private const string ArmReaderRoleId =
        "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/role-reader";
    private const string AuScope = "/administrativeUnits/au-1";

    // ---- ARM keying -------------------------------------------------------

    // The multi-path grant: the same role at the same scope reached through two
    // groups, so the two rows differ only by which eligibility produced them.
    [Fact]
    public void Deduplicate_ArmRowsSameScopeAndRole_DifferentEligibilityIds_CollapseToOne()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-a"),
            ArmRow(SubScope, eligibilityId: "elig-b"),
        });

        var row = Assert.Single(result);
        Assert.Equal(SubScope, row.ArmScope);
    }

    // The dominant source of duplicates: ListEligibleRolesAsync queries every
    // subscription and ARM returns eligibilities inherited from above the queried
    // scope, so one management-group-scoped eligibility comes back byte-identical
    // once per subscription beneath it.
    [Fact]
    public void Deduplicate_ArmFanOut_ByteIdenticalRows_CollapseToOne()
    {
        var fanOut = Enumerable.Range(0, 5).Select(_ => ArmRow(MgScope, eligibilityId: "elig-mg")).ToList();

        var row = Assert.Single(EligibleRoleDeduplicator.Deduplicate(fanOut));

        Assert.Equal(MgScope, row.ArmScope);
        Assert.Equal("elig-mg", row.EligibilityId);
    }

    // The over-collapse guard. Two subscriptions can carry different policies for
    // the same role, and activation PUTs to the scope's own URL, so these are two
    // genuinely different rows.
    [Fact]
    public void Deduplicate_ArmRowsDifferingOnlyByScope_StayApart()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope),
            ArmRow(OtherSubScope),
        });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.ArmScope == SubScope);
        Assert.Contains(result, r => r.ArmScope == OtherSubScope);
    }

    // ARM returns both the scope and the role definition path with inconsistent
    // casing and an occasional trailing slash; the key is the policy key, which
    // normalizes both, so these are the same row.
    [Fact]
    public void Deduplicate_ArmKeyNormalizesCasingAndTrailingSlash()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, roleDefinitionId: ArmReaderRoleId, eligibilityId: "elig-a"),
            ArmRow("/Subscriptions/SUB-1/", roleDefinitionId: ArmReaderRoleId.ToUpperInvariant(), eligibilityId: "elig-b"),
        });

        Assert.Single(result);
    }

    // ---- Entra keying -----------------------------------------------------

    // An absent directoryScopeId and an explicit "/" are the same directory-wide
    // scope — a cache row written before the member existed must not show up as a
    // second copy of a role the user holds once.
    [Fact]
    public void Deduplicate_EntraRowsDirectoryWideByAbsenceAndBySlash_CollapseToOne()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            EntraRow(directoryScopeId: null, eligibilityId: "elig-a"),
            EntraRow(directoryScopeId: "/", eligibilityId: "elig-b"),
        });

        Assert.Single(result);
    }

    // The over-collapse guard on the Entra side: an administrative-unit-scoped
    // eligibility and a directory-wide one for the same role are different
    // grants, and activating the wrong one is rejected.
    [Fact]
    public void Deduplicate_EntraRowsAtDifferentDirectoryScopes_StayApart()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            EntraRow(directoryScopeId: AuScope),
            EntraRow(directoryScopeId: "/"),
        });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.DirectoryScopeId == AuScope);
        Assert.Contains(result, r => r.DirectoryScopeId == "/");
    }

    [Fact]
    public void Deduplicate_EntraRoleDefinitionIdIsKeyedCaseInsensitively()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            EntraRow(roleDefinitionId: "role-owner", eligibilityId: "elig-a"),
            EntraRow(roleDefinitionId: "ROLE-OWNER", eligibilityId: "elig-b"),
        });

        Assert.Single(result);
    }

    // ---- cross-provider guard ---------------------------------------------

    // "Owner" exists in both Entra ID and Azure RBAC, and ActiveRoleAssignment
    // .Matches relies on the Source split, so the provider is part of the key —
    // even when name and role definition id both collide.
    [Fact]
    public void Deduplicate_EntraAndArmRowsSharingNameAndRoleDefinitionId_StayApart()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            EntraRow(roleName: "Owner", roleDefinitionId: "role-owner", directoryScopeId: "/"),
            ArmRow(scope: null, roleName: "Owner", roleDefinitionId: "role-owner"),
        });

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Source == PimSource.EntraId);
        Assert.Contains(result, r => r.Source == PimSource.AzureRbac);
    }

    // ---- winner rule ------------------------------------------------------

    // A Direct eligibility's id names the signed-in user; a group-derived one
    // names the group. Direct wins outright, whichever order the rows arrive in
    // and even when the group row is the one carrying a usable id.
    [Fact]
    public void Deduplicate_DirectRowWins_EvenWhenTheGroupRowComesFirstWithTheEligibilityId()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-group", memberType: "Group"),
            ArmRow(SubScope, eligibilityId: null, memberType: "Direct"),
        });

        var row = Assert.Single(result);
        Assert.Equal("Direct", row.MemberType);
        Assert.Null(row.EligibilityId);
    }

    [Fact]
    public void Deduplicate_DirectRowWins_WhenItComesFirst()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-direct", memberType: "Direct"),
            ArmRow(SubScope, eligibilityId: "elig-group", memberType: "Group"),
        });

        Assert.Equal("elig-direct", Assert.Single(result).EligibilityId);
    }

    // memberType casing is not something to depend on across ARM and Graph.
    [Fact]
    public void Deduplicate_MemberTypeIsMatchedCaseInsensitively()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-group", memberType: "Group"),
            ArmRow(SubScope, eligibilityId: "elig-direct", memberType: "direct"),
        });

        Assert.Equal("elig-direct", Assert.Single(result).EligibilityId);
    }

    // Neither row is Direct: a usable eligibility id breaks the tie, because ARM
    // activation sends it as linkedRoleEligibilityScheduleId.
    [Fact]
    public void Deduplicate_NeitherRowDirect_TheRowWithAnEligibilityIdWins()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "   ", memberType: "Group"),
            ArmRow(SubScope, eligibilityId: "elig-group", memberType: "Group"),
        });

        Assert.Equal("elig-group", Assert.Single(result).EligibilityId);
    }

    // Equal ranks keep the first row seen, which is what makes the output stable.
    [Fact]
    public void Deduplicate_EqualRanks_KeepTheFirstRowSeen()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-first", memberType: "Group"),
            ArmRow(SubScope, eligibilityId: "elig-second", memberType: "Group"),
        });

        Assert.Equal("elig-first", Assert.Single(result).EligibilityId);

        var direct = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-first", memberType: "Direct"),
            ArmRow(SubScope, eligibilityId: "elig-second", memberType: "Direct"),
        });

        Assert.Equal("elig-first", Assert.Single(direct).EligibilityId);
    }

    // ---- cap merge --------------------------------------------------------

    // Under-offering a duration is cosmetic; over-offering earns a 400.
    [Fact]
    public void Deduplicate_CapMerge_TakesTheLowestKnownCap()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-a", cap: TimeSpan.FromHours(8)),
            ArmRow(SubScope, eligibilityId: "elig-b", cap: TimeSpan.FromHours(2)),
            ArmRow(SubScope, eligibilityId: "elig-c", cap: TimeSpan.FromHours(4)),
        });

        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(result).MaxActivationDuration);
    }

    // Null is "unknown", never "unlimited" and never "shortest" — it must not
    // displace a cap the policy read actually produced, in either order.
    [Fact]
    public void Deduplicate_CapMerge_NullDoesNotDisplaceAKnownCap()
    {
        var nullFirst = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-a", cap: null),
            ArmRow(SubScope, eligibilityId: "elig-b", cap: TimeSpan.FromHours(2)),
        });
        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(nullFirst).MaxActivationDuration);

        var nullLast = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-a", cap: TimeSpan.FromHours(2)),
            ArmRow(SubScope, eligibilityId: "elig-b", cap: null),
        });
        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(nullLast).MaxActivationDuration);
    }

    // The winner is picked on rank, but the cap comes from the whole group: the
    // losing row's tighter cap still applies.
    [Fact]
    public void Deduplicate_CapMerge_UsesTheGroupsLowestCapNotTheWinnersOwn()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-group", memberType: "Group", cap: TimeSpan.FromHours(1)),
            ArmRow(SubScope, eligibilityId: "elig-direct", memberType: "Direct", cap: TimeSpan.FromHours(8)),
        });

        var row = Assert.Single(result);
        Assert.Equal("elig-direct", row.EligibilityId);
        Assert.Equal(TimeSpan.FromHours(1), row.MaxActivationDuration);
    }

    [Fact]
    public void Deduplicate_CapMerge_AllUnknown_StaysUnknown()
    {
        var result = EligibleRoleDeduplicator.Deduplicate(new[]
        {
            ArmRow(SubScope, eligibilityId: "elig-a", cap: null),
            ArmRow(SubScope, eligibilityId: "elig-b", cap: null),
        });

        Assert.Null(Assert.Single(result).MaxActivationDuration);
    }

    // ---- degenerate input -------------------------------------------------

    [Fact]
    public void Deduplicate_EmptyInput_IsEmpty()
    {
        Assert.Empty(EligibleRoleDeduplicator.Deduplicate(Array.Empty<UnifiedEligibleRole>()));
    }

    // The common case is a group of one, and it must come back as the very same
    // instance — no rebuild, so no field can be quietly rewritten.
    [Fact]
    public void Deduplicate_SingleRow_IsReturnedUntouched()
    {
        var original = ArmRow(SubScope, eligibilityId: "elig-a", memberType: "Group", cap: TimeSpan.FromHours(3));

        var row = Assert.Single(EligibleRoleDeduplicator.Deduplicate(new[] { original }));

        Assert.Same(original, row);
        Assert.Equal(original, row);
    }

    // ---- ordering ---------------------------------------------------------

    // The menu is built straight off this list, so the order must not wobble
    // between runs: groups come out in first-appearance order.
    [Fact]
    public void Deduplicate_PreservesFirstAppearanceOrder_AndIsStableAcrossRuns()
    {
        var input = new[]
        {
            ArmRow(OtherSubScope, eligibilityId: "elig-b"),
            EntraRow(roleDefinitionId: "role-owner", eligibilityId: "elig-owner"),
            ArmRow(SubScope, eligibilityId: "elig-a1"),
            ArmRow(OtherSubScope, eligibilityId: "elig-b2"),
            EntraRow(roleDefinitionId: "role-owner", eligibilityId: "elig-owner-2"),
            ArmRow(SubScope, eligibilityId: "elig-a2"),
        };

        var first = Keys(EligibleRoleDeduplicator.Deduplicate(input));
        var second = Keys(EligibleRoleDeduplicator.Deduplicate(input));

        Assert.Equal(
            new[]
            {
                $"{PimSource.AzureRbac}|{OtherSubScope}",
                $"{PimSource.EntraId}|role-owner",
                $"{PimSource.AzureRbac}|{SubScope}",
            },
            first);
        Assert.Equal(first, second);
    }

    // ---- builders ---------------------------------------------------------

    private static List<string> Keys(IEnumerable<UnifiedEligibleRole> roles)
        => roles.Select(r => $"{r.Source}|{r.ArmScope ?? r.RoleDefinitionId}").ToList();

    private static UnifiedEligibleRole ArmRow(
        string? scope,
        string roleName = "Reader",
        string roleDefinitionId = ArmReaderRoleId,
        string? eligibilityId = "elig-arm",
        string? memberType = "Direct",
        TimeSpan? cap = null)
        => new(
            Source: PimSource.AzureRbac,
            RoleName: roleName,
            RoleDefinitionId: roleDefinitionId,
            ScopeDisplay: "Dev sub",
            ArmScope: scope,
            EligibilityId: eligibilityId,
            MaxActivationDuration: cap,
            MemberType: memberType,
            DirectoryScopeId: null);

    private static UnifiedEligibleRole EntraRow(
        string roleName = "Global Reader",
        string roleDefinitionId = "role-global-reader",
        string? directoryScopeId = "/",
        string? eligibilityId = "elig-entra",
        string? memberType = "Direct",
        TimeSpan? cap = null)
        => new(
            Source: PimSource.EntraId,
            RoleName: roleName,
            RoleDefinitionId: roleDefinitionId,
            ScopeDisplay: EntraDirectoryScope.DisplayFor(directoryScopeId),
            ArmScope: null,
            EligibilityId: eligibilityId,
            MaxActivationDuration: cap,
            MemberType: memberType,
            DirectoryScopeId: directoryScopeId);
}
