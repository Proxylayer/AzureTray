namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Contract version gate. Plugins declare <see cref="ITrayPlugin.ApiVersion"/>
/// equal to <see cref="Current"/> and the host rejects any plugin whose
/// declared value does not match its own. The value is bumped only on breaking
/// changes — minor host releases keep loading existing plugins unchanged.
/// </summary>
public static class PluginApiVersion
{
    /// <summary>The current contract version. Declare this in <see cref="ITrayPlugin.ApiVersion"/>.</summary>
    public const int Current = 2;
}
