namespace AzureTray.Plugin.PIM.Arm.Dto;

internal sealed record ArmScheduleRequestStatus(
    string? Id,
    string? Name,
    ArmScheduleRequestStatusProperties? Properties);

internal sealed record ArmScheduleRequestStatusProperties(string? Status);
