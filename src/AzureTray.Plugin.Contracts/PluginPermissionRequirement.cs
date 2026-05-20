namespace AzureTray.Plugin.Contracts;

/// <summary>
/// One delegated OAuth scope that a plugin needs the host's app registration
/// to expose and admin-consent in each managed tenant.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScopeName"/> is the wire name
/// (e.g. <c>"User.Read"</c>, <c>"user_impersonation"</c>);
/// <see cref="ScopeId"/> is the GUID of the corresponding
/// <c>oauth2PermissionScope</c> on the resource service principal in the tenant.
/// Well-known Microsoft APIs use the same scope GUIDs across all tenants;
/// plugin authors look them up once and bake them in.
/// </para>
/// <para>
/// <strong>Security — least privilege:</strong> only declare scopes you
/// actually call. Each declared scope triggers a consent prompt; unnecessary
/// scopes erode user trust and are flagged by the host's permission auditor.
/// </para>
/// </remarks>
public sealed record PluginPermissionRequirement(
    PermissionApi Api,
    string ScopeName,
    string ScopeId,
    string DisplayName);

/// <summary>The Microsoft API family a <see cref="PluginPermissionRequirement"/> targets.</summary>
public enum PermissionApi
{
    /// <summary>Microsoft Graph (<c>https://graph.microsoft.com</c>).</summary>
    MicrosoftGraph,
    /// <summary>Azure Resource Manager (<c>https://management.azure.com</c>).</summary>
    AzureResourceManager,
}
