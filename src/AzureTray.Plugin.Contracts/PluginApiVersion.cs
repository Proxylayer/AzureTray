namespace AzureTray.Plugin.Contracts;

// Plugins must declare the contract version they were built against (via
// ITrayPlugin.ApiVersion). The host rejects plugins whose declared version
// does not match Current. Bump only on breaking changes to the contract.
public static class PluginApiVersion
{
    public const int Current = 1;
}
