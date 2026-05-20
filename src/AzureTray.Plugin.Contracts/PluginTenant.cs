namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Read-only projection of a tenant for plugin consumption.
/// </summary>
/// <remarks>
/// <c>ClientId</c> and credentials are intentionally omitted — they are managed
/// by the host and accessed only through
/// <see cref="IPluginHttpClient.SendAsync"/>.
/// </remarks>
public sealed record PluginTenant(string TenantId, string DisplayName);
