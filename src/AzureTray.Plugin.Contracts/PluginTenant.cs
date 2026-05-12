namespace AzureTray.Plugin.Contracts;

// Read-only projection of a tenant for plugin consumption. ClientId is
// deliberately omitted — credentials are managed by the host and reached
// through IPluginHttpClient.
public sealed record PluginTenant(string TenantId, string DisplayName);
