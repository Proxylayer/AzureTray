using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Extensions;

// Discovers plugins by querying nuget.org's search API for packages
// tagged with the host's discovery tag (see NuGetPluginFeedOptions).
// The tag is the gate: anything that doesn't carry it doesn't appear
// in the in-app browser, period.
public interface INuGetPluginFeed
{
    // Fetches the package list and returns the listed plugins.
    //
    // forceRefresh: when true, skips the in-memory cache TTL and re-hits
    // nuget.org. The min-interval throttle still applies so a runaway
    // caller can't flood the search endpoint. Use for explicit user
    // "Refresh" actions; leave false for opportunistic calls.
    Task<IReadOnlyList<NuGetPluginEntry>> FetchAsync(
        string? query,
        bool includePrerelease,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}

// One plugin returned by the NuGet search. Versions are stored newest-first.
//
// Property names match the previous registry entry shape so the existing
// SettingsWindow.xaml data templates keep binding without changes.
public sealed record NuGetPluginEntry(
    string Id,
    string DisplayName,
    string? Publisher,
    string? PublisherUrl,
    string? Description,
    IReadOnlyList<string> Tags,
    string? SourceRepo,
    string? IconUrl,
    // Always the same as Id for the NuGet-tag path — kept on the record
    // because the install flow already plumbs it through to the GHSA scan.
    string? NuGetPackageId,
    IReadOnlyList<NuGetPluginVersion> Versions);

public sealed record NuGetPluginVersion(
    string Version,
    string? PublishedUtc,
    string? MinHostVersion,
    string DownloadUrl,
    string? ChecksumSha256);
