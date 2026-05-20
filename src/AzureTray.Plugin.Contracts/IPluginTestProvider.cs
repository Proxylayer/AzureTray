using System.Collections.Generic;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Optional interface for plugins that expose runnable health-check or
/// smoke tests. The host's admin menu lists these alongside its own
/// built-in tests so users can verify both host plumbing and plugin
/// features from one place.
/// </summary>
/// <remarks>
/// Each <see cref="PluginTest"/> captures whatever context it needs via
/// closure so the host runner stays generic — it just calls
/// <see cref="PluginTest.Run"/> and displays the
/// <see cref="PluginTestResult"/>.
/// </remarks>
public interface IPluginTestProvider
{
    /// <summary>The tests this plugin exposes.</summary>
    IReadOnlyList<PluginTest> Tests { get; }
}
