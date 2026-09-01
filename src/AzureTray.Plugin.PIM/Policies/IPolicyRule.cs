namespace AzureTray.Plugin.PIM.Policies;

// The two rules PolicyRules reads. Named rather than passed as service-specific
// strings so each DTO keeps its own spelling of the type name next to the shape
// it belongs to.
internal enum PolicyRuleKind
{
    Expiration,
    Approval,
}

// What a role-management policy rule has to expose for the activation-relevant
// readers to work, whichever service produced it. Graph and ARM model a rule as
// structurally identical records — an id, a maximum duration, and an approval
// flag nested under a setting — and differ in one thing only: how the rule type
// is discriminated. Graph carries an @odata.type that is a namespaced type name
// and may be absent; ARM carries an exact RuleType string. MatchesRuleType is
// where that difference lives, so PolicyRules can be written once.
internal interface IPolicyRule
{
    string? Id { get; }

    string? MaximumDuration { get; }

    bool? IsApprovalRequired { get; }

    bool MatchesRuleType(PolicyRuleKind kind);
}
