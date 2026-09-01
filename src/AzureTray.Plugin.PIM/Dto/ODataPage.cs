using System.Collections.Generic;
using System.Text.Json.Serialization;
using AzureTray.Plugin.PIM.Http;

namespace AzureTray.Plugin.PIM.Dto;

internal sealed record ODataPage<T>(
    List<T>? Value,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink) : IPageEnvelope<T>;
