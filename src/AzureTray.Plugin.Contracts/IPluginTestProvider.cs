using System.Collections.Generic;

namespace AzureTray.Plugin.Contracts;

// Optional. A plugin that wants to expose runnable tests (smoke, sanity,
// or live API probes) implements this and returns the tests it offers.
// The host's admin menu lists them alongside its own built-in tests so the
// user can verify both host plumbing and plugin features from one place.
public interface IPluginTestProvider
{
    IReadOnlyList<PluginTest> Tests { get; }
}
