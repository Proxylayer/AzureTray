namespace AzureTray.Plugin.PIM.Arm.Dto;

// /subscriptions response shape (subset).
internal sealed record ArmSubscription(
    string? Id,
    string? SubscriptionId,
    string? DisplayName,
    string? State);
