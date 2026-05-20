using System;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// One runnable health-check or smoke test exposed by an
/// <see cref="IPluginTestProvider"/>. The <see cref="Run"/> delegate captures
/// whatever context the test needs so the host's test runner can stay generic.
/// </summary>
public sealed record PluginTest(
    string Name,
    string? Description,
    Func<CancellationToken, Task<PluginTestResult>> Run);

/// <summary>Outcome of a <see cref="PluginTest"/> run.</summary>
public sealed record PluginTestResult(bool Passed, string? Message = null)
{
    /// <summary>Creates a passing result with an optional message.</summary>
    public static PluginTestResult Pass(string? message = null) => new(true, message);

    /// <summary>Creates a failing result with a required explanation.</summary>
    public static PluginTestResult Fail(string message) => new(false, message);
}
