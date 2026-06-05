namespace AzureTray.Dto;

// Subset of Microsoft Graph /v1.0/me response we actually consume.
// Wire shape — JSON deserialized with camelCase naming policy.
//
// Mail is the user's real, routable email address. For B2B guests it holds the
// home-tenant email (e.g. jbland@proxylayer.net) while UserPrincipalName holds
// the synthetic "#EXT#" form — so Mail is the value worth using as a LoginHint.
// It can be null (a user without a mailbox), hence optional.
public sealed record MeResponse(string Id, string DisplayName, string UserPrincipalName, string? Mail = null);
