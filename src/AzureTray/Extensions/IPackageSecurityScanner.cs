using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Extensions;

// Pre-install vulnerability scan for a NuGet plugin package.
// Implementations query a public advisory database (currently GHSA)
// for known vulnerabilities affecting the exact package + version
// being installed. The install flow blocks on High/Critical findings
// (with a user-confirm-anyway override) and surfaces lower-severity
// findings as a warning that doesn't block.
public interface IPackageSecurityScanner
{
    Task<PackageSecurityScanResult> ScanAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken);
}

public enum AdvisorySeverity
{
    Unknown = 0,
    Low,
    Medium,
    High,
    Critical,
}

public sealed record SecurityAdvisory(
    string Id,                  // e.g. "GHSA-xxxx-xxxx-xxxx"
    AdvisorySeverity Severity,
    string Summary,
    string? Url,                // browser link to the advisory
    string? AffectedRange);     // e.g. ">= 1.0.0, < 2.5.3" (verbatim from GHSA)

public sealed record PackageSecurityScanResult(
    string PackageId,
    string Version,
    IReadOnlyList<SecurityAdvisory> Advisories,
    bool ScanSucceeded,         // false = couldn't reach the database; treat as "unknown", not "clean"
    string? ScanError)          // diagnostic message when ScanSucceeded is false
{
    public bool HasCriticalOrHigh
        => Advisories.Any(a => a.Severity is AdvisorySeverity.Critical or AdvisorySeverity.High);

    public bool HasAny => Advisories.Count > 0;
}
