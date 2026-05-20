namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Well-known <c>clientName</c> constants for
/// <see cref="IPluginHttpClient.SendAsync"/>.
/// The host configures matching named <see cref="System.Net.Http.HttpClient"/>
/// registrations (base address + resilience handler).
/// </summary>
public static class PluginHttpClientNames
{
    /// <summary>
    /// Microsoft Graph API client (<c>https://graph.microsoft.com</c>).
    /// Use with <see cref="IPluginContext.GraphScope"/>.
    /// </summary>
    public const string Graph = "graph";

    /// <summary>
    /// Azure Resource Manager client (<c>https://management.azure.com</c>).
    /// Use with <see cref="IPluginContext.ArmScope"/>.
    /// </summary>
    public const string Arm = "arm";
}
