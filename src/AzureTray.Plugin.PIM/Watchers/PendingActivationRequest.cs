using System;

namespace AzureTray.Plugin.PIM.Watchers;

// An activation the signed-in user submitted that did not go live immediately —
// it went to approval. Tracked until it is provisioned, terminally refused, or
// too old to care about. Serialized to disk, so keep it a flat record.
internal sealed record PendingActivationRequest(
    PimSource Source,
    string RequestId,
    string RoleName,
    string ScopeDisplay,
    string? ArmScope,           // Required to poll ARM request status; null for Entra ID.
    DateTimeOffset SubmittedAt);
