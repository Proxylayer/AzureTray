using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AzureTray.Plugin.PIM.Dto;

// RoleDefinitionId is a bare GUID here (unlike ARM, where it is a full
// resource path). Policy is populated only when the request expands it.
internal sealed record EntraPolicyAssignment(
    string? Id,
    string? PolicyId,
    string? RoleDefinitionId,
    string? ScopeId,
    string? ScopeType,
    EntraRoleManagementPolicy? Policy);

// Two navigation properties carry the same rule shapes, and which one a caller
// gets back depends on which one it expanded. Directory roles expand
// effectiveRules — effective rules account for policy the tenant enforces
// directory-wide on top of the role's own policy. PIM for Groups expands rules:
// that is the form Microsoft documents for a Group-scoped policy assignment and
// the form verified to parse against a live tenant, and groups have no
// directory-wide overlay for effectiveRules to fold in. Exactly one of the two
// is ever populated on a given response — read them through RulesToRead.
internal sealed record EntraRoleManagementPolicy(
    string? Id,
    List<EntraPolicyRule>? EffectiveRules,
    List<EntraPolicyRule>? Rules = null)
{
    // Whichever rule collection this response actually expanded, or null when
    // the request expanded neither (rules stay unknown; the caller must not
    // read that as "no restrictions").
    public List<EntraPolicyRule>? RulesToRead => EffectiveRules ?? Rules;
}

// Union of the rule shapes we read, kept flat rather than modelled as a
// polymorphic hierarchy: Graph's effectiveRules array mixes 17 rule types,
// and System.Text.Json's polymorphic reader is sensitive to where the
// discriminator sits in the payload. Rules are identified by Id
// ("Expiration_EndUser_Assignment", "Approval_EndUser_Assignment") with
// ODataType as a corroborating check. MaximumDuration is an ISO-8601 duration
// carried directly on the rule; approval settings nest under Setting.
internal sealed record EntraPolicyRule(
    [property: JsonPropertyName("@odata.type")] string? ODataType,
    string? Id,
    string? MaximumDuration,
    bool? IsExpirationRequired,
    EntraApprovalRuleSetting? Setting);

internal sealed record EntraApprovalRuleSetting(
    bool? IsApprovalRequired,
    bool? IsApprovalRequiredForExtension,
    List<EntraApprovalStage>? ApprovalStages);

internal sealed record EntraApprovalStage(
    bool? IsApproverJustificationRequired,
    int? ApprovalStageTimeOutInDays);
