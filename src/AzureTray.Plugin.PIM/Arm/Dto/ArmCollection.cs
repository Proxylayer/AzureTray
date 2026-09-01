using System.Collections.Generic;
using AzureTray.Plugin.PIM.Http;

namespace AzureTray.Plugin.PIM.Arm.Dto;

internal sealed record ArmCollection<T>(List<T>? Value, string? NextLink) : IPageEnvelope<T>;
