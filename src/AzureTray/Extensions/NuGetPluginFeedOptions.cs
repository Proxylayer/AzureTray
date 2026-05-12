namespace AzureTray.Extensions;

// Controls how the host queries nuget.org for plugin packages. Packages
// must carry the DiscoveryTag in their PackageTags to show up — that's
// the gate keeping arbitrary NuGet packages out of the in-app browser.
public sealed class NuGetPluginFeedOptions
{
    public const string SectionName = "App:NuGet";

    // The package tag the host filters by. Plugin authors must add this
    // to their .csproj's <PackageTags>. Tightly bounded so the search
    // result set is exclusively AzureTray plugins.
    public string DiscoveryTag { get; init; } = "proxylayer.azuretray-plugin";

    // NuGet v3 search endpoint. The well-known production URL is fine for
    // public packages; override to point at a corporate NuGet feed mirror.
    public string SearchUrl { get; init; } =
        "https://azuresearch-usnc.nuget.org/query";

    // Rate-limit knobs. Public NuGet search tolerates client-side caching
    // well; we just want to avoid pounding it on every Browse-button tap.
    public int CacheTtlSeconds { get; init; } = 60;
    public int MinFetchIntervalSeconds { get; init; } = 5;

    // When true (default), prereleases (e.g. '-preview.X' versions) are
    // included in the per-plugin version list. The UI exposes this
    // as a checkbox; this controls the default state.
    public bool IncludePrereleaseByDefault { get; init; } = true;

    // Cap on packages returned per query. NuGet search defaults to 20;
    // raise modestly for ecosystems that grow past that.
    public int MaxResults { get; init; } = 50;
}
