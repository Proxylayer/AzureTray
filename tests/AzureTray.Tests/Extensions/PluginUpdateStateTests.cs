using System;
using System.Collections.Generic;
using System.Linq;
using AzureTray.Extensions;
using Xunit;

namespace AzureTray.Tests.Extensions;

// The bridge between the background poll and the TRANSIENT SettingsViewModel.
// Unsubscribe has to work: the view model subscribes in its constructor and
// detaches in Cleanup(), so a handler that survives detach keeps a dead window
// alive and re-runs its refresh on every poll.
public sealed class PluginUpdateStateTests
{
    [Fact]
    public void Available_StartsEmpty()
        => Assert.Empty(new PluginUpdateState().Available);

    [Fact]
    public void Publish_UpdatesTheSnapshotAndRaisesChanged()
    {
        var state = new PluginUpdateState();
        var raised = new List<IReadOnlyList<PluginUpdate>>();
        state.Changed += raised.Add;

        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.1.0")]);

        Assert.Single(state.Available);
        Assert.Equal("Acme.Plugin.Foo", state.Available[0].PackageId);
        var payload = Assert.Single(raised);
        Assert.Equal("1.1.0", payload[0].LatestVersion);
    }

    [Fact]
    public void Publish_DoesNotRaiseForTheSameSetAgain()
    {
        var state = new PluginUpdateState();
        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.1.0")]);

        var raised = 0;
        state.Changed += _ => raised++;

        // An hourly poll that keeps finding the same update must not churn the UI.
        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.1.0")]);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Publish_RaisesWhenTheVersionChangesForTheSamePackage()
    {
        var state = new PluginUpdateState();
        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.1.0")]);

        var raised = 0;
        state.Changed += _ => raised++;

        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.2.0")]);

        Assert.Equal(1, raised);
        Assert.Equal("1.2.0", state.Available[0].LatestVersion);
    }

    [Fact]
    public void Publish_RaisesWhenTheSetIsClearedToEmpty()
    {
        var state = new PluginUpdateState();
        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.1.0")]);

        var raised = 0;
        state.Changed += _ => raised++;

        state.Publish(Array.Empty<PluginUpdate>());

        Assert.Equal(1, raised);
        Assert.Empty(state.Available);
    }

    [Fact]
    public void Publish_TakesAnIndependentSnapshotOfTheCallersList()
    {
        var state = new PluginUpdateState();
        var mutable = new List<PluginUpdate> { Update("Acme.Plugin.Foo", "1.0.0", "1.1.0") };

        state.Publish(mutable);
        mutable.Clear();

        Assert.Single(state.Available);
    }

    [Fact]
    public void Publish_ThrowsOnNull()
        => Assert.Throws<ArgumentNullException>(() => new PluginUpdateState().Publish(null!));

    [Fact]
    public void Unsubscribe_StopsFurtherCallbacks()
    {
        var state = new PluginUpdateState();
        var raised = 0;
        void Handler(IReadOnlyList<PluginUpdate> _) => raised++;

        state.Changed += Handler;
        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.1.0")]);
        Assert.Equal(1, raised);

        state.Changed -= Handler;
        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.2.0")]);
        state.Publish([Update("Acme.Plugin.Bar", "1.0.0", "1.3.0")]);

        Assert.Equal(1, raised);
        // Detaching the handler must not stop the snapshot itself from moving.
        Assert.Equal("Acme.Plugin.Bar", state.Available[0].PackageId);
    }

    [Fact]
    public void Unsubscribe_LeavesOtherSubscribersAttached()
    {
        var state = new PluginUpdateState();
        var goneCount = 0;
        var stayCount = 0;
        void Gone(IReadOnlyList<PluginUpdate> _) => goneCount++;
        void Stay(IReadOnlyList<PluginUpdate> _) => stayCount++;

        state.Changed += Gone;
        state.Changed += Stay;
        state.Changed -= Gone;

        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.1.0")]);

        Assert.Equal(0, goneCount);
        Assert.Equal(1, stayCount);
    }

    [Fact]
    public void Remove_DropsOnePackageAndRaisesChanged()
    {
        var state = new PluginUpdateState();
        state.Publish(
        [
            Update("Acme.Plugin.Foo", "1.0.0", "1.1.0"),
            Update("Acme.Plugin.Bar", "2.0.0", "2.1.0"),
        ]);

        var raised = 0;
        state.Changed += _ => raised++;

        state.Remove("acme.plugin.foo");   // case-insensitive

        Assert.Equal(1, raised);
        Assert.Equal("Acme.Plugin.Bar", Assert.Single(state.Available).PackageId);
    }

    [Fact]
    public void Remove_IsANoOpForAnUnknownOrBlankPackageId()
    {
        var state = new PluginUpdateState();
        state.Publish([Update("Acme.Plugin.Foo", "1.0.0", "1.1.0")]);

        var raised = 0;
        state.Changed += _ => raised++;

        state.Remove("Not.Installed");
        state.Remove("   ");
        state.Remove(null!);

        Assert.Equal(0, raised);
        Assert.Single(state.Available);
    }

    private static PluginUpdate Update(string packageId, string installed, string latest)
    {
        var version = new NuGetPluginVersion(
            latest, null, null, $"https://nuget/{packageId}/{latest}.nupkg", null);
        var entry = new NuGetPluginEntry(
            Id: packageId,
            DisplayName: packageId,
            Publisher: null,
            PublisherUrl: null,
            Description: null,
            Tags: Array.Empty<string>(),
            SourceRepo: null,
            IconUrl: null,
            NuGetPackageId: packageId,
            Versions: [version]);

        return new PluginUpdate(
            PackageId: packageId,
            PluginId: null,
            InstalledVersion: installed,
            InstalledDllPath: $@"C:\plugins\{packageId}\{packageId}.dll",
            Entry: entry,
            Latest: version);
    }
}
