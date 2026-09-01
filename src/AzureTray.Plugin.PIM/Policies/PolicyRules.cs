using System;
using System.Collections.Generic;
using System.Linq;

namespace AzureTray.Plugin.PIM.Policies;

// Reads the two policy rules that bear on a user self-activating, for every PIM
// source. The logic was identical for Entra directory roles, PIM for Groups and
// ARM long before it lived in one place; only the rule-type discriminator ever
// differed, and that now sits on the rule itself (see IPolicyRule).
internal static class PolicyRules
{
    // Rule ids inside a role management policy. They are the same strings for
    // directory roles and for PIM for Groups — the policy resource is shared,
    // only the scopeType on the assignment differs — and ARM spells them the
    // same way, so one pair of constants serves all three.
    public const string EndUserExpirationRuleId = "Expiration_EndUser_Assignment";
    public const string EndUserApprovalRuleId = "Approval_EndUser_Assignment";

    // Expiration_EndUser_Assignment is the only rule that governs a user
    // self-activating an eligible role or group access. The other expiration
    // rules (Expiration_Admin_Eligibility, Expiration_Admin_Assignment) are
    // days-scale caps on eligibility/assignment and must never stand in for it.
    // Matched on id + rule type, never on target — ARM and Graph disagree on the
    // casing of target values.
    public static TimeSpan? ReadMaxActivationDuration<TRule>(List<TRule> rules)
        where TRule : class, IPolicyRule
    {
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.Id, EndUserExpirationRuleId, StringComparison.OrdinalIgnoreCase)
            && r.MatchesRuleType(PolicyRuleKind.Expiration));

        return Iso8601Duration.TryParse(rule?.MaximumDuration);
    }

    public static bool? ReadApprovalRequired<TRule>(List<TRule> rules)
        where TRule : class, IPolicyRule
    {
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.Id, EndUserApprovalRuleId, StringComparison.OrdinalIgnoreCase)
            && r.MatchesRuleType(PolicyRuleKind.Approval));

        return rule?.IsApprovalRequired;
    }
}
