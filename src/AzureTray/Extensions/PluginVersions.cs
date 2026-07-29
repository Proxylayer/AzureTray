using System;
using System.Collections.Generic;
using System.Linq;
using NuGet.Versioning;

namespace AzureTray.Extensions;

// SemVer handling for plugin packages. Parsing and precedence come from
// NuGet.Versioning (the same implementation nuget.org itself uses), so
// prerelease ordering (1.0.0-beta.2 < 1.0.0-beta.10 < 1.0.0) and build
// metadata (1.0.0+abc == 1.0.0) behave the way package authors expect.
//
// Nothing here indexes into a version list: the NuGet v3 search response
// documents no ordering guarantee for a hit's `versions` array, so "newest"
// is always something we compute.
internal static class PluginVersions
{
    public static bool TryParse(string? raw, out NuGetVersion version)
    {
        if (!string.IsNullOrWhiteSpace(raw) && NuGetVersion.TryParse(raw.Trim(), out var parsed))
        {
            version = parsed;
            return true;
        }

        version = null!;
        return false;
    }

    // Newest-first ordering for display. Unparseable entries can't be
    // ordered meaningfully, so they keep their wire order at the end of the
    // list rather than being dropped — the install path still works for
    // them, only comparison is off the table.
    public static List<NuGetPluginVersion> SortNewestFirst(IEnumerable<NuGetPluginVersion> versions)
    {
        var parsed = new List<(NuGetPluginVersion Entry, NuGetVersion Version)>();
        var unparsed = new List<NuGetPluginVersion>();

        foreach (var candidate in versions)
        {
            if (TryParse(candidate.Version, out var parsedVersion))
            {
                parsed.Add((candidate, parsedVersion));
            }
            else
            {
                unparsed.Add(candidate);
            }
        }

        var ordered = parsed
            .OrderByDescending(p => p.Version, VersionComparer.VersionRelease)
            .Select(p => p.Entry)
            .ToList();
        ordered.AddRange(unparsed);
        return ordered;
    }

    // Highest version the entry offers, or null when it offers none that
    // parse. Prereleases are excluded unless the user opted into them —
    // the feed query already filters them server-side, this is the
    // client-side half of the same rule.
    public static NuGetPluginVersion? SelectLatest(NuGetPluginEntry entry, bool includePrerelease)
    {
        ArgumentNullException.ThrowIfNull(entry);

        NuGetPluginVersion? best = null;
        NuGetVersion? bestParsed = null;

        foreach (var candidate in entry.Versions)
        {
            if (!TryParse(candidate.Version, out var parsed)) continue;
            if (parsed.IsPrerelease && !includePrerelease) continue;
            if (bestParsed is not null && VersionComparer.VersionRelease.Compare(parsed, bestParsed) <= 0) continue;

            best = candidate;
            bestParsed = parsed;
        }

        return best;
    }

    // True when `candidate` supersedes `installed`. A prerelease only
    // supersedes a release when the user opted into prereleases.
    public static bool IsNewer(NuGetVersion candidate, NuGetVersion installed, bool includePrerelease)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(installed);

        if (candidate.IsPrerelease && !includePrerelease) return false;
        return VersionComparer.VersionRelease.Compare(candidate, installed) > 0;
    }
}
