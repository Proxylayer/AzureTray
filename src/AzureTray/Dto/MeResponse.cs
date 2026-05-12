namespace AzureTray.Dto;

// Subset of Microsoft Graph /v1.0/me response we actually consume.
// Wire shape — JSON deserialized with camelCase naming policy.
public sealed record MeResponse(string Id, string DisplayName, string UserPrincipalName);
