using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Extensions;

// Compares the versions installed under plugins/ against what nuget.org
// currently offers for the same package ids. Plugins ship independently of
// the host (Velopack updates the host only), so without this a user on the
// newest host can sit on a months-old plugin with no signal at all.
public interface IPluginUpdateChecker
{
    // One entry per installed plugin that has a strictly newer version on the
    // feed. Returns empty — never throws — when nothing is installed, the
    // feed is unreachable, or no version can be parsed on either side.
    Task<IReadOnlyList<PluginUpdate>> CheckAsync(CancellationToken cancellationToken);
}

// A newer version found for an installed plugin. Carries the feed entry and
// the chosen version so callers can hand them straight back to the ordinary
// install path — the update flow is an install of a specific version, not a
// separate mechanism.
public sealed record PluginUpdate(
    string PackageId,
    string? PluginId,
    string InstalledVersion,
    string InstalledDllPath,
    NuGetPluginEntry Entry,
    NuGetPluginVersion Latest)
{
    public string LatestVersion => Latest.Version;

    public string DownloadUrl => Latest.DownloadUrl;

    public string DisplayName => string.IsNullOrWhiteSpace(Entry.DisplayName) ? PackageId : Entry.DisplayName;

    // "Foo Plugin  0.8.0 → 0.9.0" — one line per plugin in the aggregated toast.
    public string SummaryLine => $"{DisplayName}  {InstalledVersion} → {LatestVersion}";
}
