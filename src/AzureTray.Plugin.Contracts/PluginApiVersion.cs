namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Contract version gate. A plugin declares <see cref="ITrayPlugin.ApiVersion"/>
/// and the host loads it when that value falls within the supported range
/// [<see cref="MinSupported"/>, <see cref="Current"/>]. Use
/// <see cref="IsSupported(int)"/> to test a value against the range.
/// </summary>
/// <remarks>
/// <para>Evolution policy:</para>
/// <list type="bullet">
/// <item><description>
/// Additive, binary-compatible surface changes (new default-interface members,
/// new optional capability interfaces, new init-only DTO properties) bump
/// <see cref="Current"/> and leave <see cref="MinSupported"/> alone, so plugins
/// built against any version still in the window keep loading.
/// </description></item>
/// <item><description>
/// A genuinely breaking change raises <see cref="MinSupported"/> to lock out the
/// now-incompatible older plugins — the host logs the rejection with the range.
/// </description></item>
/// </list>
/// <para>
/// The contracts assembly keeps a fixed <c>AssemblyVersion</c>, so an old
/// plugin always binds to the host's current copy at runtime; this range is the
/// only thing that decides whether that copy will load it.
/// </para>
/// </remarks>
public static class PluginApiVersion
{
    /// <summary>
    /// The newest contract version. New plugins should declare this in
    /// <see cref="ITrayPlugin.ApiVersion"/>.
    /// </summary>
    public const int Current = 2;

    /// <summary>
    /// The oldest contract version the host still loads. Raise this only when
    /// dropping support for an old contract shape (a breaking change).
    /// </summary>
    public const int MinSupported = 1;

    /// <summary>
    /// True when a plugin declaring <paramref name="apiVersion"/> is loadable by
    /// this host — i.e. it falls within
    /// [<see cref="MinSupported"/>, <see cref="Current"/>].
    /// </summary>
    public static bool IsSupported(int apiVersion)
        => apiVersion >= MinSupported && apiVersion <= Current;
}
