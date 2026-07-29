using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AzureTray.Extensions;
using Xunit;

namespace AzureTray.Tests.Extensions;

// The NuGet v3 search response documents NO ordering for a hit's `versions`
// array. The mapper used to reverse it, which silently depends on nuget.org
// happening to send ascending order; these pin the parsed sort instead.
public sealed class NuGetPluginFeedOrderingTests
{
    private const string Tag = "proxylayer.azuretray-plugin";

    [Theory]
    // Ascending — today's observed nuget.org behaviour.
    [InlineData("0.9.0", "1.0.0", "1.9.0", "1.10.0", "2.0.0")]
    // Descending.
    [InlineData("2.0.0", "1.10.0", "1.9.0", "1.0.0", "0.9.0")]
    // Shuffled.
    [InlineData("1.9.0", "2.0.0", "0.9.0", "1.10.0", "1.0.0")]
    [InlineData("1.10.0", "0.9.0", "2.0.0", "1.0.0", "1.9.0")]
    public async Task FetchAsync_ReturnsVersionsNewestFirstWhateverTheWireOrder(params string[] wireOrder)
    {
        using var feed = NewFeed(SearchJson("Acme.Plugin.Foo", wireOrder));

        var entries = await feed.FetchAsync(null, includePrerelease: false, CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal(
            new[] { "2.0.0", "1.10.0", "1.9.0", "1.0.0", "0.9.0" },
            entry.Versions.Select(v => v.Version).ToArray());
    }

    [Fact]
    public async Task FetchAsync_OrdersPrereleasesBelowTheirRelease()
    {
        using var feed = NewFeed(SearchJson(
            "Acme.Plugin.Foo",
            ["1.0.0-beta.2", "1.0.0", "1.0.0-alpha", "1.0.0-beta.10"]));

        var entries = await feed.FetchAsync(null, includePrerelease: true, CancellationToken.None);

        Assert.Equal(
            new[] { "1.0.0", "1.0.0-beta.10", "1.0.0-beta.2", "1.0.0-alpha" },
            Assert.Single(entries).Versions.Select(v => v.Version).ToArray());
    }

    [Fact]
    public async Task FetchAsync_BuildsTheFlatContainerUrlForTheNewestVersion()
    {
        using var feed = NewFeed(SearchJson("Acme.Plugin.Foo", ["1.0.0", "2.0.0"]));

        var entry = Assert.Single(await feed.FetchAsync(null, false, CancellationToken.None));

        Assert.Equal(
            "https://api.nuget.org/v3-flatcontainer/acme.plugin.foo/2.0.0/acme.plugin.foo.2.0.0.nupkg",
            entry.Versions[0].DownloadUrl);
    }

    [Fact]
    public async Task FetchAsync_FallsBackToTheRolledUpVersionFieldWhenVersionsIsEmpty()
    {
        var json = $$"""
            {
              "totalHits": 1,
              "data": [
                {
                  "id": "Acme.Plugin.Foo",
                  "version": "3.1.4",
                  "title": "Foo Plugin",
                  "tags": ["{{Tag}}"],
                  "versions": []
                }
              ]
            }
            """;
        using var feed = NewFeed(json);

        var entry = Assert.Single(await feed.FetchAsync(null, false, CancellationToken.None));

        var version = Assert.Single(entry.Versions);
        Assert.Equal("3.1.4", version.Version);
        Assert.Equal(
            "https://api.nuget.org/v3-flatcontainer/acme.plugin.foo/3.1.4/acme.plugin.foo.3.1.4.nupkg",
            version.DownloadUrl);
    }

    [Fact]
    public async Task FetchAsync_FallsBackToTheRolledUpVersionFieldWhenVersionsIsAbsent()
    {
        var json = $$"""
            {
              "totalHits": 1,
              "data": [
                { "id": "Acme.Plugin.Foo", "version": "3.1.4", "tags": ["{{Tag}}"] }
              ]
            }
            """;
        using var feed = NewFeed(json);

        var entry = Assert.Single(await feed.FetchAsync(null, false, CancellationToken.None));

        Assert.Equal("3.1.4", Assert.Single(entry.Versions).Version);
    }

    [Fact]
    public async Task FetchAsync_KeepsUnparseableVersionsButRanksThemLast()
    {
        using var feed = NewFeed(SearchJson("Acme.Plugin.Foo", ["not-a-version", "1.0.0", "2.0.0"]));

        var entry = Assert.Single(await feed.FetchAsync(null, false, CancellationToken.None));

        Assert.Equal(
            new[] { "2.0.0", "1.0.0", "not-a-version" },
            entry.Versions.Select(v => v.Version).ToArray());
    }

    [Fact]
    public async Task FetchAsync_DropsPackagesWithoutTheDiscoveryTag()
    {
        var json = """
            {
              "totalHits": 1,
              "data": [
                {
                  "id": "Random.Package",
                  "version": "1.0.0",
                  "tags": ["something-else"],
                  "versions": [{ "version": "1.0.0" }]
                }
              ]
            }
            """;
        using var feed = NewFeed(json);

        Assert.Empty(await feed.FetchAsync(null, false, CancellationToken.None));
    }

    private static string SearchJson(string packageId, params string[] versions)
    {
        var versionArray = string.Join(", ", versions.Select(v => $"{{ \"version\": \"{v}\" }}"));
        return $$"""
            {
              "totalHits": 1,
              "data": [
                {
                  "id": "{{packageId}}",
                  "version": "{{versions[0]}}",
                  "title": "Foo Plugin",
                  "description": "Does Foo things.",
                  "authors": ["Acme"],
                  "tags": ["{{Tag}}"],
                  "versions": [{{versionArray}}]
                }
              ]
            }
            """;
    }

    private static NuGetPluginFeed NewFeed(string json)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(NuGetPluginFeed.HttpClientName)
            .Returns(_ => new HttpClient(new StaticJsonHandler(json)));

        return new NuGetPluginFeed(
            factory,
            Options.Create(new NuGetPluginFeedOptions { DiscoveryTag = Tag }),
            NullLogger<NuGetPluginFeed>.Instance);
    }

    private sealed class StaticJsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StaticJsonHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
    }
}
