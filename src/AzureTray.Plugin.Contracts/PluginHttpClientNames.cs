namespace AzureTray.Plugin.Contracts;

// Stable client names for IPluginHttpClient.SendAsync. The host configures
// matching named HttpClient registrations (BaseAddress + resilience handler).
public static class PluginHttpClientNames
{
    public const string Graph = "graph";
    public const string Arm = "arm";
}
