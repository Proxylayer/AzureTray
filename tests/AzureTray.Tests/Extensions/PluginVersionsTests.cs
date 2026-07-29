using System;
using System.Collections.Generic;
using System.Linq;
using AzureTray.Extensions;
using Xunit;

namespace AzureTray.Tests.Extensions;

// SemVer precedence and ordering for plugin packages. These exist because the
// version that used to back the update check assumed nuget.org returns a hit's
// `versions` array oldest-first and just reversed it — the v3 search spec
// documents no ordering at all, so "newest" has to be computed.
public sealed class PluginVersionsTests
{
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    [InlineData("1.0.0-alpha")]
    [InlineData("1.0.0-beta.10")]
    [InlineData("1.0.0+build.5")]
    [InlineData("1.2.3.4")]
    [InlineData("  1.0.0  ")]
    [InlineData("\t2.1.0\n")]
    public void TryParse_AcceptsSemVerAndTrimsWhitespace(string raw)
    {
        Assert.True(PluginVersions.TryParse(raw, out var parsed));
        Assert.NotNull(parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("latest")]
    [InlineData("1.0.0-")]
    [InlineData("v1.0.0")]      // tag-style prefix: never a NuGet version.
    [InlineData("version 1")]
    public void TryParse_RejectsNullEmptyAndGarbage(string? raw)
    {
        Assert.False(PluginVersions.TryParse(raw, out _));
    }

    [Theory]
    // SemVer 2.0 §11: a prerelease has lower precedence than its release.
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    // Numeric identifiers compare numerically, not lexically.
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10")]
    // Numeric identifiers always have lower precedence than alphanumeric ones.
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    // Ordinary numeric ordering, including the two-digit trap.
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0", "2.0.0")]
    public void IsNewer_FollowsSemVerPrecedence(string lower, string higher)
    {
        Assert.True(PluginVersions.TryParse(lower, out var low));
        Assert.True(PluginVersions.TryParse(higher, out var high));

        Assert.True(PluginVersions.IsNewer(high, low, includePrerelease: true));
        Assert.False(PluginVersions.IsNewer(low, high, includePrerelease: true));
    }

    [Fact]
    public void IsNewer_IgnoresBuildMetadata()
    {
        Assert.True(PluginVersions.TryParse("1.0.0+abc", out var abc));
        Assert.True(PluginVersions.TryParse("1.0.0+def", out var def));

        // Build metadata is not part of precedence: neither supersedes the
        // other, so a republished package with a new build stamp must not be
        // reported as an update.
        Assert.False(PluginVersions.IsNewer(abc, def, includePrerelease: true));
        Assert.False(PluginVersions.IsNewer(def, abc, includePrerelease: true));
    }

    [Fact]
    public void IsNewer_IsFalseForEqualVersions()
    {
        Assert.True(PluginVersions.TryParse("1.4.2", out var a));
        Assert.True(PluginVersions.TryParse("1.4.2", out var b));

        Assert.False(PluginVersions.IsNewer(a, b, includePrerelease: true));
    }

    [Fact]
    public void IsNewer_PrereleaseSupersedesReleaseOnlyWhenOptedIn()
    {
        Assert.True(PluginVersions.TryParse("1.0.0", out var installed));
        Assert.True(PluginVersions.TryParse("1.1.0-beta.1", out var candidate));

        Assert.False(PluginVersions.IsNewer(candidate, installed, includePrerelease: false));
        Assert.True(PluginVersions.IsNewer(candidate, installed, includePrerelease: true));
    }

    [Fact]
    public void IsNewer_ThrowsOnNullArguments()
    {
        Assert.True(PluginVersions.TryParse("1.0.0", out var version));

        Assert.Throws<ArgumentNullException>(() => PluginVersions.IsNewer(null!, version, false));
        Assert.Throws<ArgumentNullException>(() => PluginVersions.IsNewer(version, null!, false));
    }

    // The whole point: the same set in any wire order yields one ordering.
    [Fact]
    public void SortNewestFirst_ProducesTheSameOrderForEveryInputPermutation()
    {
        string[] expected =
        [
            "2.0.0",
            "1.10.0",
            "1.9.0",
            "1.0.0",
            "1.0.0-beta.10",
            "1.0.0-beta.2",
            "1.0.0-alpha",
        ];

        string[][] permutations =
        [
            // Ascending — what nuget.org happens to send today.
            [.. expected.Reverse()],
            // Already descending.
            expected,
            // Shuffled two different ways.
            ["1.0.0-beta.2", "2.0.0", "1.0.0", "1.9.0", "1.0.0-alpha", "1.10.0", "1.0.0-beta.10"],
            ["1.10.0", "1.0.0-alpha", "1.0.0-beta.10", "2.0.0", "1.0.0-beta.2", "1.0.0", "1.9.0"],
        ];

        foreach (var permutation in permutations)
        {
            var sorted = PluginVersions.SortNewestFirst(permutation.Select(Version));

            Assert.Equal(expected, sorted.Select(v => v.Version).ToArray());
        }
    }

    [Fact]
    public void SortNewestFirst_KeepsUnparseableEntriesAtTheEndInWireOrder()
    {
        var sorted = PluginVersions.SortNewestFirst(
        [
            Version("garbage"),
            Version("1.0.0"),
            Version("still-garbage"),
            Version("2.0.0"),
        ]);

        Assert.Equal(
            new[] { "2.0.0", "1.0.0", "garbage", "still-garbage" },
            sorted.Select(v => v.Version).ToArray());
    }

    [Fact]
    public void SortNewestFirst_ReturnsEmptyForEmptyInput()
        => Assert.Empty(PluginVersions.SortNewestFirst(Array.Empty<NuGetPluginVersion>()));

    [Fact]
    public void SelectLatest_PicksHighestRegardlessOfPosition()
    {
        var entry = Entry("1.0.0", "3.1.4", "2.0.0");

        var latest = PluginVersions.SelectLatest(entry, includePrerelease: false);

        Assert.NotNull(latest);
        Assert.Equal("3.1.4", latest!.Version);
    }

    [Fact]
    public void SelectLatest_ExcludesPrereleasesUnlessOptedIn()
    {
        var entry = Entry("1.0.0", "2.0.0-beta.1");

        Assert.Equal("1.0.0", PluginVersions.SelectLatest(entry, includePrerelease: false)!.Version);
        Assert.Equal("2.0.0-beta.1", PluginVersions.SelectLatest(entry, includePrerelease: true)!.Version);
    }

    [Fact]
    public void SelectLatest_ReturnsNullWhenNothingParseableIsOffered()
    {
        Assert.Null(PluginVersions.SelectLatest(Entry("garbage", "also-garbage"), includePrerelease: true));
        Assert.Null(PluginVersions.SelectLatest(Entry(), includePrerelease: true));
        // Prerelease-only feed with prereleases opted out leaves nothing eligible.
        Assert.Null(PluginVersions.SelectLatest(Entry("1.0.0-beta.1"), includePrerelease: false));
    }

    [Fact]
    public void SelectLatest_ThrowsOnNullEntry()
        => Assert.Throws<ArgumentNullException>(() => PluginVersions.SelectLatest(null!, false));

    private static NuGetPluginVersion Version(string version)
        => new(version, PublishedUtc: null, MinHostVersion: null, DownloadUrl: $"https://nuget/{version}.nupkg", ChecksumSha256: null);

    private static NuGetPluginEntry Entry(params string[] versions)
        => new(
            Id: "Acme.Plugin.Foo",
            DisplayName: "Foo Plugin",
            Publisher: "Acme",
            PublisherUrl: null,
            Description: null,
            Tags: Array.Empty<string>(),
            SourceRepo: null,
            IconUrl: null,
            NuGetPackageId: "Acme.Plugin.Foo",
            Versions: versions.Select(Version).ToArray());
}
