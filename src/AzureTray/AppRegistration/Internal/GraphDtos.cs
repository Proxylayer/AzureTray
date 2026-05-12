using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AzureTray.AppRegistration.Internal;

internal sealed record ODataPage<T>(
    List<T>? Value,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

internal sealed record GraphApplication(
    string? Id,
    string? AppId,
    string? DisplayName,
    List<RequiredResourceAccessDto>? RequiredResourceAccess,
    PublicClientDto? PublicClient = null);

// Subset of Microsoft Graph's application.publicClient. Only the redirect
// URIs field is needed — MSAL's WAM broker requires
// "ms-appx-web://microsoft.aad.brokerplugin/{appId}" be listed here.
internal sealed record PublicClientDto(
    [property: JsonPropertyName("redirectUris")] List<string>? RedirectUris);

internal sealed record RequiredResourceAccessDto(
    string? ResourceAppId,
    List<ResourceAccessDto>? ResourceAccess);

internal sealed record ResourceAccessDto(string? Id, string? Type);

internal sealed record GraphServicePrincipal(
    string? Id,
    string? AppId,
    string? DisplayName);

internal sealed record OAuth2PermissionGrant(
    string? Id,
    string? ClientId,
    string? ConsentType,
    string? ResourceId,
    string? Scope,
    string? PrincipalId);
