using System;
using AzureTray.Extensions;
using Xunit;

namespace AzureTray.Tests.Extensions;

// The UI-bindable row. EffectiveVersion prefers the install manifest because
// that record survives a DLL that failed to load — the case where there is no
// live plugin instance to read a version from at all.
public sealed class InstalledExtensionTests
{
    [Fact]
    public void WithNoUpdate_UpdateAffordancesAreHidden()
    {
        var row = Row(installedVersion: "1.0.0", availableUpdateVersion: null);

        Assert.False(row.HasUpdate);
        Assert.Equal(string.Empty, row.UpdateButtonText);
        Assert.Equal(string.Empty, row.UpdateHint);
    }

    [Fact]
    public void WithAnUpdate_ButtonAndHintNameTheTargetVersion()
    {
        var row = Row(installedVersion: "1.0.0", availableUpdateVersion: "1.2.0");

        Assert.True(row.HasUpdate);
        Assert.Contains("1.2.0", row.UpdateButtonText, StringComparison.Ordinal);
        Assert.Equal("Update to v1.2.0", row.UpdateButtonText);
        Assert.Contains("1.2.0", row.UpdateHint, StringComparison.Ordinal);
        Assert.Contains("nuget.org", row.UpdateHint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithABlankUpdateVersion_ThereIsNoUpdate(string? availableUpdateVersion)
    {
        var row = Row(installedVersion: "1.0.0", availableUpdateVersion: availableUpdateVersion);

        Assert.False(row.HasUpdate);
        Assert.Equal(string.Empty, row.UpdateButtonText);
    }

    [Fact]
    public void APluginPendingUninstall_NeverOffersAnUpdate()
    {
        var row = Row(installedVersion: "1.0.0", availableUpdateVersion: "1.2.0", isPendingUninstall: true);

        Assert.False(row.HasUpdate);
        Assert.Equal(string.Empty, row.UpdateButtonText);
        Assert.Equal("Pending uninstall.", row.StatusDisplay);
    }

    [Fact]
    public void EffectiveVersion_PrefersTheManifestVersion()
    {
        var row = Row(installedVersion: "2.0.0", availableUpdateVersion: null, loadedVersion: "1.0.0");

        Assert.Equal("2.0.0", row.EffectiveVersion);
    }

    [Fact]
    public void EffectiveVersion_FallsBackToTheLoadedVersionWithoutAManifest()
    {
        var row = Row(installedVersion: null, availableUpdateVersion: null, loadedVersion: "1.0.0");

        Assert.Equal("1.0.0", row.EffectiveVersion);
    }

    [Fact]
    public void EffectiveVersion_IsNullWhenNeitherIsKnown()
    {
        var row = Row(installedVersion: null, availableUpdateVersion: null, loadedVersion: null);

        Assert.Null(row.EffectiveVersion);
        // The status line still has to render something for the user.
        Assert.Contains("v?", row.StatusDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusDisplay_UsesEffectiveVersionForLoadedAndUnloadedRows()
    {
        var loaded = Row(installedVersion: "2.0.0", availableUpdateVersion: null, loadedVersion: "1.0.0", isLoaded: true);
        Assert.Equal("Foo Plugin  v2.0.0", loaded.StatusDisplay);

        // A DLL that failed to load has no live instance; the manifest is the
        // only reason a version can be shown at all here.
        var broken = Row(installedVersion: "2.0.0", availableUpdateVersion: null, loadedVersion: null, isLoaded: false);
        Assert.StartsWith("Installed v2.0.0", broken.StatusDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateMembersDefaultToOffWhenTheTrailingArgumentsAreOmitted()
    {
        // Existing call sites build a row without any update information.
        var row = new InstalledExtension(
            "Acme.Plugin.Foo.dll",
            @"C:\plugins\Acme.Plugin.Foo\Acme.Plugin.Foo.dll",
            IsPendingUninstall: false,
            IsLoaded: true,
            PluginId: "com.acme.foo",
            LoadedDisplayName: "Foo Plugin",
            LoadedVersion: "1.0.0");

        Assert.Null(row.PackageId);
        Assert.Null(row.InstalledVersion);
        Assert.Null(row.AvailableUpdateVersion);
        Assert.False(row.HasUpdate);
        Assert.Equal("1.0.0", row.EffectiveVersion);
    }

    private static InstalledExtension Row(
        string? installedVersion,
        string? availableUpdateVersion,
        string? loadedVersion = "1.0.0",
        bool isLoaded = true,
        bool isPendingUninstall = false)
        => new(
            FileName: "Acme.Plugin.Foo.dll",
            FullPath: @"C:\plugins\Acme.Plugin.Foo\Acme.Plugin.Foo.dll",
            IsPendingUninstall: isPendingUninstall,
            IsLoaded: isLoaded,
            PluginId: "com.acme.foo",
            LoadedDisplayName: "Foo Plugin",
            LoadedVersion: loadedVersion,
            PackageId: "Acme.Plugin.Foo",
            InstalledVersion: installedVersion,
            AvailableUpdateVersion: availableUpdateVersion);
}
