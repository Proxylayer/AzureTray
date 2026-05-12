using System.Collections.Generic;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Testing;

// Aggregates runnable tests from two sources for the admin Test Runner UI:
//   * Built-in host tests (notifications, clipboard, logger, badge).
//   * Tests contributed by loaded plugins via IPluginTestProvider.
//
// Returned once per call so freshly-loaded plugins show up immediately.
public interface ITestRegistry
{
    IReadOnlyList<TestGroup> GetGroups();
}

public sealed record TestGroup(string Name, IReadOnlyList<PluginTest> Tests);
