using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureTray.Extensions;

public sealed class NuGetPluginFeed : INuGetPluginFeed, IDisposable
{
    public const string HttpClientName = "nuget-search";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly NuGetPluginFeedOptions _options;
    private readonly ILogger<NuGetPluginFeed> _logger;

    // Rate-limit state — same pattern as the old registry fetcher.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedSearch? _cached;

    public NuGetPluginFeed(
        IHttpClientFactory httpFactory,
        IOptions<NuGetPluginFeedOptions> options,
        ILogger<NuGetPluginFeed> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NuGetPluginEntry>> FetchAsync(
        string? query,
        bool includePrerelease,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var normalizedQuery = (query ?? string.Empty).Trim();
        var entries = await GetEntriesAsync(normalizedQuery, includePrerelease, forceRefresh, cancellationToken).ConfigureAwait(false);
        return entries ?? Array.Empty<NuGetPluginEntry>();
    }

    private async Task<IReadOnlyList<NuGetPluginEntry>?> GetEntriesAsync(string query, bool includePrerelease, bool forceRefresh, CancellationToken cancellationToken)
    {
        var key = new SearchKey(query, includePrerelease);
        var ttl = TimeSpan.FromSeconds(Math.Max(0, _options.CacheTtlSeconds));
        var minInterval = TimeSpan.FromSeconds(Math.Max(0, _options.MinFetchIntervalSeconds));
        var now = DateTimeOffset.UtcNow;

        var snapshot = _cached;
        if (!forceRefresh && snapshot is not null && snapshot.Key == key && now - snapshot.FetchedUtc < ttl)
        {
            return snapshot.Entries;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = _cached;
            now = DateTimeOffset.UtcNow;
            if (!forceRefresh && snapshot is not null && snapshot.Key == key && now - snapshot.FetchedUtc < ttl)
            {
                return snapshot.Entries;
            }

            if (snapshot is not null)
            {
                var elapsed = now - snapshot.FetchedUtc;
                if (elapsed < minInterval)
                {
                    var waitFor = minInterval - elapsed;
                    _logger.LogDebug(
                        "NuGet feed fetch throttled — sleeping {WaitMs} ms before next call.",
                        (int)waitFor.TotalMilliseconds);
                    await Task.Delay(waitFor, cancellationToken).ConfigureAwait(false);
                }
            }

            var fresh = await FetchLiveAsync(key, cancellationToken).ConfigureAwait(false);
            if (fresh is not null)
            {
                _cached = new CachedSearch(DateTimeOffset.UtcNow, key, fresh);
                return fresh;
            }

            // Live fetch failed — fall back to whatever we cached last time.
            return snapshot is not null && snapshot.Key == key ? snapshot.Entries : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<NuGetPluginEntry>?> FetchLiveAsync(SearchKey key, CancellationToken cancellationToken)
    {
        // The discovery tag is the gate. We pass it as `tags:"X"` which
        // NuGet's search service interprets as an exact-tag match; the
        // user's free-text query is OR'ed in after. Without the tag,
        // any package on nuget.org could match the q parameter; with it,
        // only properly-marked plugins surface.
        var tagFilter = $"tags:\"{_options.DiscoveryTag}\"";
        var fullQuery = string.IsNullOrEmpty(key.Query) ? tagFilter : $"{tagFilter} {key.Query}";
        var url = $"{_options.SearchUrl}?q={Uri.EscapeDataString(fullQuery)}&prerelease={(key.IncludePrerelease ? "true" : "false")}&take={Math.Max(1, _options.MaxResults)}&semVerLevel=2.0.0";

        var client = _httpFactory.CreateClient(HttpClientName);
        try
        {
            var doc = await client.GetFromJsonAsync<SearchResponse>(url, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (doc?.Data is null) return Array.Empty<NuGetPluginEntry>();

            // Defence in depth: even though we filter by tag on the wire,
            // re-check on the client so a NuGet search misbehaviour can't
            // sneak in an unrelated package. Mismatches are dropped with a
            // log line, not silently — surfaces query-tag bugs early.
            var entries = new List<NuGetPluginEntry>();
            foreach (var dto in doc.Data)
            {
                var entry = MapEntry(dto);
                if (entry is null) continue;
                if (!entry.Tags.Any(t => string.Equals(t, _options.DiscoveryTag, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning(
                        "Dropping {PackageId} from results: search returned it but it doesn't carry the discovery tag '{Tag}'.",
                        entry.Id, _options.DiscoveryTag);
                    continue;
                }
                entries.Add(entry);
            }
            return entries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query nuget.org search at {Url}.", url);
            return null;
        }
    }

    private static NuGetPluginEntry? MapEntry(SearchHit hit)
    {
        if (string.IsNullOrWhiteSpace(hit.Id)) return null;

        // Newest-first. NuGet's search returns versions oldest-first; flip.
        var versions = new List<NuGetPluginVersion>();
        if (hit.Versions is { Count: > 0 })
        {
            // Each version comes back with @id pointing at the registration
            // blob; the predictable flat-container URL is what we actually
            // download from, so synthesise it from id + version.
            var lowerId = hit.Id.ToLowerInvariant();
            foreach (var v in hit.Versions)
            {
                if (string.IsNullOrWhiteSpace(v.Version)) continue;
                var lowerVersion = v.Version.ToLowerInvariant();
                versions.Add(new NuGetPluginVersion(
                    Version: v.Version,
                    PublishedUtc: null,
                    MinHostVersion: null,
                    DownloadUrl: $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg",
                    ChecksumSha256: null));
            }
            versions.Reverse();
        }
        if (versions.Count == 0 && !string.IsNullOrWhiteSpace(hit.Version))
        {
            // Some search responses include only the rolled-up Version
            // field — handle that case so we don't drop the package.
            var lowerId = hit.Id.ToLowerInvariant();
            var lowerVersion = hit.Version!.ToLowerInvariant();
            versions.Add(new NuGetPluginVersion(
                Version: hit.Version!,
                PublishedUtc: null,
                MinHostVersion: null,
                DownloadUrl: $"https://api.nuget.org/v3-flatcontainer/{lowerId}/{lowerVersion}/{lowerId}.{lowerVersion}.nupkg",
                ChecksumSha256: null));
        }
        if (versions.Count == 0) return null;

        var publisher = hit.Authors is { Count: > 0 }
            ? string.Join(", ", hit.Authors)
            : null;

        return new NuGetPluginEntry(
            Id: hit.Id,
            DisplayName: string.IsNullOrWhiteSpace(hit.Title) ? hit.Id : hit.Title!,
            Publisher: publisher,
            PublisherUrl: hit.ProjectUrl,
            Description: hit.Description ?? hit.Summary,
            Tags: (IReadOnlyList<string>?)hit.Tags ?? Array.Empty<string>(),
            SourceRepo: hit.ProjectUrl,
            IconUrl: hit.IconUrl,
            NuGetPackageId: hit.Id,
            Versions: versions);
    }

    public void Dispose() => _gate.Dispose();

    private sealed record SearchKey(string Query, bool IncludePrerelease);
    private sealed record CachedSearch(DateTimeOffset FetchedUtc, SearchKey Key, IReadOnlyList<NuGetPluginEntry> Entries);

    // ─── NuGet v3 search response wire shape ────────────────────────────

    private sealed class SearchResponse
    {
        [JsonPropertyName("totalHits")]
        public int TotalHits { get; init; }

        [JsonPropertyName("data")]
        public List<SearchHit>? Data { get; init; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }

        [JsonPropertyName("iconUrl")]
        public string? IconUrl { get; init; }

        [JsonPropertyName("projectUrl")]
        public string? ProjectUrl { get; init; }

        [JsonPropertyName("authors")]
        public List<string>? Authors { get; init; }

        [JsonPropertyName("tags")]
        public List<string>? Tags { get; init; }

        [JsonPropertyName("versions")]
        public List<SearchVersion>? Versions { get; init; }
    }

    private sealed class SearchVersion
    {
        [JsonPropertyName("version")]
        public string? Version { get; init; }
    }
}
