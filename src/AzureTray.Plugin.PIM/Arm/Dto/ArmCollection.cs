using System.Collections.Generic;

namespace AzureTray.Plugin.PIM.Arm.Dto;

internal sealed record ArmCollection<T>(List<T>? Value, string? NextLink);
