namespace AzureTray.Plugin.Contracts;

// One delegated OAuth scope that a plugin needs the host's app registration to
// expose and have admin-consented in each managed tenant.
//
// ScopeName is the wire name ("User.Read", "user_impersonation"); ScopeId is the
// GUID of the corresponding oauth2PermissionScope on the resource service
// principal in the tenant. Plugin authors look the IDs up once and bake them in;
// well-known Microsoft APIs use the same scope IDs across all tenants.
public sealed record PluginPermissionRequirement(
    PermissionApi Api,
    string ScopeName,
    string ScopeId,
    string DisplayName);

public enum PermissionApi
{
    MicrosoftGraph,
    AzureResourceManager,
}
