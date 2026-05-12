using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AzureTray.Extensions;

// Queries the GitHub Advisory Database via api.github.com/advisories.
// Anonymous access is allowed for public advisories — no key needed —
// at a rate of 60 requests/hour per IP, which is plenty for the
// install-time scan since each install is a single call.
//
// We return all advisories the API associates with the package, then
// let the caller (typically the Install command) decide which severity
// triggers a hard block vs. a warning.
public sealed class GhsaPackageSecurityScanner : IPackageSecurityScanner
{
    public const string HttpClientName = "ghsa";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GhsaPackageSecurityScanner> _logger;

    public GhsaPackageSecurityScanner(
        IHttpClientFactory httpFactory,
        ILogger<GhsaPackageSecurityScanner> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<PackageSecurityScanResult> ScanAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        // The 'affects' filter narrows server-side to advisories that
        // explicitly cite this package + version. Without it we'd see
        // every advisory in the NuGet ecosystem.
        var affects = Uri.EscapeDataString($"{packageId}@{version}");
        var url = $"https://api.github.com/advisories?ecosystem=nuget&affects={affects}";

        var client = _httpFactory.CreateClient(HttpClientName);

        try
        {
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // GHSA returns 404 when the package id isn't known at all
                // (private package, never seen, etc.). Treat as clean —
                // not "scan failed" — because there are simply no known
                // advisories to block on.
                return new PackageSecurityScanResult(packageId, version, Array.Empty<SecurityAdvisory>(), ScanSucceeded: true, ScanError: null);
            }
            response.EnsureSuccessStatusCode();

            var advisories = await response.Content
                .ReadFromJsonAsync<List<GhsaAdvisory>>(JsonOptions, cancellationToken)
                .ConfigureAwait(false)
                ?? new List<GhsaAdvisory>();

            var mapped = new List<SecurityAdvisory>();
            foreach (var advisory in advisories)
            {
                if (string.IsNullOrEmpty(advisory.GhsaId)) continue;
                mapped.Add(new SecurityAdvisory(
                    Id: advisory.GhsaId,
                    Severity: MapSeverity(advisory.Severity),
                    Summary: advisory.Summary ?? "(no summary)",
                    Url: advisory.HtmlUrl,
                    AffectedRange: FindAffectedRange(advisory, packageId)));
            }

            return new PackageSecurityScanResult(packageId, version, mapped, ScanSucceeded: true, ScanError: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GHSA vulnerability scan failed for {PackageId} {Version}. Install will continue without coverage.",
                packageId, version);
            return new PackageSecurityScanResult(packageId, version, Array.Empty<SecurityAdvisory>(), ScanSucceeded: false, ScanError: ex.Message);
        }
    }

    private static AdvisorySeverity MapSeverity(string? raw) => raw?.ToLowerInvariant() switch
    {
        "critical" => AdvisorySeverity.Critical,
        "high"     => AdvisorySeverity.High,
        "medium"   => AdvisorySeverity.Medium,
        "moderate" => AdvisorySeverity.Medium,
        "low"      => AdvisorySeverity.Low,
        _          => AdvisorySeverity.Unknown,
    };

    private static string? FindAffectedRange(GhsaAdvisory advisory, string packageId)
    {
        if (advisory.Vulnerabilities is null) return null;
        foreach (var vuln in advisory.Vulnerabilities)
        {
            if (vuln.Package is null) continue;
            if (!string.Equals(vuln.Package.Ecosystem, "nuget", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(vuln.Package.Name, packageId, StringComparison.OrdinalIgnoreCase)) continue;
            return vuln.VulnerableVersionRange;
        }
        return null;
    }

    // ─── GHSA wire shapes ──────────────────────────────────────────────
    // https://docs.github.com/en/rest/security-advisories/global-advisories

    private sealed class GhsaAdvisory
    {
        [JsonPropertyName("ghsa_id")]
        public string? GhsaId { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("severity")]
        public string? Severity { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("vulnerabilities")]
        public List<GhsaVulnerability>? Vulnerabilities { get; init; }
    }

    private sealed class GhsaVulnerability
    {
        [JsonPropertyName("package")]
        public GhsaPackageRef? Package { get; init; }

        [JsonPropertyName("vulnerable_version_range")]
        public string? VulnerableVersionRange { get; init; }
    }

    private sealed class GhsaPackageRef
    {
        [JsonPropertyName("ecosystem")]
        public string? Ecosystem { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
