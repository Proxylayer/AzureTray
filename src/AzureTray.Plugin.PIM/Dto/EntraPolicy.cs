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

// EffectiveRules rather than Rules: effective rules account for policy the
// tenant enforces directory-wide on top of the role's own policy.
internal sealed record EntraRoleManagementPolicy(
    string? Id,
    List<EntraPolicyRule>? EffectiveRules);

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
