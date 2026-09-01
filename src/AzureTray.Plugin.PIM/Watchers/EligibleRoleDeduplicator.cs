using System;
using System.Collections.Generic;
using System.Linq;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Groups;

namespace AzureTray.Plugin.PIM.Watchers;

// Collapses eligibility rows that name the same role at the same scope into one
// menu row. Two independent sources produce them:
//
//   * Multi-path grants — a role reachable through two groups or two access
//     packages comes back once per path, since ARM's
//     $filter=assignedTo('{id}') and Graph's principalId filter both include
//     inherited eligibilities.
//   * The ARM fan-out — ListEligibleRolesAsync queries every subscription and
//     ARM also returns eligibilities inherited from above the queried scope, so
//     one management-group-scoped eligibility comes back once per subscription
//     beneath that management group. Usually the dominant source.
//
// Deduplication is always within a provider: an Entra role and an ARM role can
// share a display name, and ActiveRoleAssignment.Matches relies on the Source
// split, so PimSource is part of the key.
internal static class EligibleRoleDeduplicator
{
    private const string DirectMemberType = "Direct";

    public static List<UnifiedEligibleRole> Deduplicate(IEnumerable<UnifiedEligibleRole> roles)
        => roles
            .GroupBy(KeyFor)
            .Select(Collapse)
            .ToList();

    // ARM rows key on the role's *policy* key — the exact type
    // AttachArmCapsAsync looks policies up with — so rows that collapse together
    // are provably governed by the same policy. Entra rows key on role
    // definition plus directory scope, which keeps an administrative-unit-scoped
    // eligibility separate from a directory-wide one.
    private static (PimSource Source, string Scope, string RoleDefinitionId) KeyFor(
        UnifiedEligibleRole role)
    {
        if (role.Source == PimSource.AzureRbac)
        {
            var policyKey = ArmRolePolicyKey.For(role.ArmScope, role.RoleDefinitionId);
            return (role.Source, policyKey.Scope, policyKey.RoleDefinitionId);
        }

        // Group rows key on the group plus the access id, which is also their
        // policy key: each onboarded group carries one policy per access id, so
        // rows that collapse together are provably governed by the same policy.
        // RoleDefinitionId holds the access id here, and it is far from unique —
        // dropping the group id would collapse every "Member" row in the tenant
        // into one.
        if (role.Source == PimSource.EntraGroup)
        {
            var policyKey = GroupRolePolicyKey.For(role.GroupId, role.RoleDefinitionId);
            return (role.Source, policyKey.GroupId, policyKey.AccessId);
        }

        return (
            role.Source,
            EntraDirectoryScope.NormalizeForKey(role.DirectoryScopeId),
            role.RoleDefinitionId.Trim().ToLowerInvariant());
    }

    private static UnifiedEligibleRole Collapse(
        IGrouping<(PimSource Source, string Scope, string RoleDefinitionId), UnifiedEligibleRole> group)
    {
        UnifiedEligibleRole? winner = null;
        var winnerRank = int.MinValue;
        TimeSpan? cap = null;
        var count = 0;

        foreach (var candidate in group)
        {
            count++;
            var rank = Rank(candidate);
            if (winner is null || rank > winnerRank)
            {
                winner = candidate;
                winnerRank = rank;
            }
            cap = Lower(cap, candidate.MaxActivationDuration);
        }

        // GroupBy never yields an empty group, so winner is set.
        return count == 1 ? winner! : winner! with { MaxActivationDuration = cap };
    }

    // Which of two byte-similar rows to keep. Only EligibilityId genuinely
    // differs between duplicates (role name and scope display are identical),
    // and ARM activation sends it as linkedRoleEligibilityScheduleId — where a
    // Direct eligibility's principalId is the signed-in user while a
    // group-derived one's is the *group*. Whether ARM accepts the latter paired
    // with a user principalId is unverified, so Direct wins outright; a usable
    // id breaks the remaining tie, and equal ranks keep the first row seen.
    private static int Rank(UnifiedEligibleRole role)
        => (IsDirect(role) ? 2 : 0) + (string.IsNullOrWhiteSpace(role.EligibilityId) ? 0 : 1);

    private static bool IsDirect(UnifiedEligibleRole role)
        => string.Equals(role.MemberType, DirectMemberType, StringComparison.OrdinalIgnoreCase);

    // Caps cannot disagree within a group today — the dedup key is the policy
    // key (ARM) or finer than it (Entra) — but if they ever did, under-offering
    // a duration is cosmetic while over-offering earns a 400. Null is "unknown"
    // and must never displace a known cap.
    private static TimeSpan? Lower(TimeSpan? left, TimeSpan? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return right.Value < left.Value ? right : left;
    }
}
