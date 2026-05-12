using System;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugin.Contracts;

// One runnable test. The Run delegate captures whatever context the test
// needs (typically the plugin's IPluginContext) so the host's test runner
// can stay generic — it just invokes Run and reports the outcome.
public sealed record PluginTest(
    string Name,
    string? Description,
    Func<CancellationToken, Task<PluginTestResult>> Run);

public sealed record PluginTestResult(bool Passed, string? Message = null)
{
    public static PluginTestResult Pass(string? message = null) => new(true, message);
    public static PluginTestResult Fail(string message) => new(false, message);
}
