using System;
using System.Reflection;

namespace AzureTray;

// The host's own version, as plugins and the log narrative see it: the
// AssemblyInformationalVersion with SourceLink's '+commitSha' suffix stripped,
// so what remains parses as a SemVer / System.Version. Resolved once — the
// attribute cannot change while the process runs.
internal static class AppVersion
{
    public static string? Semantic { get; } = Resolve();

    // For log lines, where "unknown" reads better than an empty string.
    public static string Display => Semantic ?? "unknown";

    private static string? Resolve()
    {
        var asm = typeof(AppVersion).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            // SourceLink appends '+commitSha' for diagnostic provenance; the
            // SemVer prefix is what callers actually want for version compares.
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }
        return asm.GetName().Version?.ToString();
    }
}
