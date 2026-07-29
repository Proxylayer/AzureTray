using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The store persists to DataDir, so every test gets its own temp directory —
// with an empty DataDir the relative path would drop JSON into the test
// working directory and leak state between tests.
public sealed class PendingActivationStoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    private static readonly PluginTenant Tenant = new("tenant-1", "Contoso");

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        var store = new PendingActivationStore(NewContext(), Tenant);

        Assert.Empty(store.Current);
    }

    [Fact]
    public void CorruptFile_IsTreatedAsACacheMiss_NotAnException()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(StorePath, "{ this is not valid json ][");

        var store = new PendingActivationStore(NewContext(), Tenant);

        Assert.Empty(store.Current);
    }

    [Fact]
    public void EmptyFile_IsTreatedAsACacheMiss_NotAnException()
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(StorePath, string.Empty);

        var store = new PendingActivationStore(NewContext(), Tenant);

        Assert.Empty(store.Current);
    }

    [Fact]
    public void Track_PersistsUnderDataDir_AndRoundTrips()
    {
        var submitted = DateTimeOffset.UtcNow.AddMinutes(-3);
        var store = new PendingActivationStore(NewContext(), Tenant);

        store.Track(Request("req-entra", PimSource.EntraId, armScope: null, submitted));
        store.Track(Request("req-arm", PimSource.AzureRbac, "/subscriptions/sub-1", submitted));

        Assert.True(File.Exists(StorePath), $"expected the store file at {StorePath}");

        var reloaded = new PendingActivationStore(NewContext(), Tenant);

        Assert.Equal(2, reloaded.Current.Count);

        var entra = reloaded.Current.Single(r => r.RequestId == "req-entra");
        Assert.Equal(PimSource.EntraId, entra.Source);
        Assert.Equal("Owner", entra.RoleName);
        Assert.Equal("Entra ID directory", entra.ScopeDisplay);
        Assert.Null(entra.ArmScope);
        Assert.Equal(submitted, entra.SubmittedAt);

        var arm = reloaded.Current.Single(r => r.RequestId == "req-arm");
        Assert.Equal(PimSource.AzureRbac, arm.Source);
        Assert.Equal("/subscriptions/sub-1", arm.ArmScope);
    }

    [Fact]
    public void Track_IgnoresDuplicateRequestId()
    {
        var store = new PendingActivationStore(NewContext(), Tenant);

        store.Track(Request("req-1", PimSource.EntraId, null, DateTimeOffset.UtcNow));
        store.Track(Request("req-1", PimSource.EntraId, null, DateTimeOffset.UtcNow));

        Assert.Single(store.Current);
    }

    [Fact]
    public void Track_IgnoresBlankRequestId()
    {
        var store = new PendingActivationStore(NewContext(), Tenant);

        store.Track(Request("   ", PimSource.EntraId, null, DateTimeOffset.UtcNow));

        Assert.Empty(store.Current);
    }

    [Fact]
    public void StopTracking_RemovesTheEntry_AndPersistsTheRemoval()
    {
        var store = new PendingActivationStore(NewContext(), Tenant);
        store.Track(Request("req-1", PimSource.EntraId, null, DateTimeOffset.UtcNow));
        store.Track(Request("req-2", PimSource.EntraId, null, DateTimeOffset.UtcNow));

        store.StopTracking("req-1");

        Assert.Single(store.Current);
        Assert.Single(new PendingActivationStore(NewContext(), Tenant).Current);
    }

    [Fact]
    public void StopTracking_UnknownRequestId_IsANoOp()
    {
        var store = new PendingActivationStore(NewContext(), Tenant);
        store.Track(Request("req-1", PimSource.EntraId, null, DateTimeOffset.UtcNow));

        store.StopTracking("req-nope");

        Assert.Single(store.Current);
    }

    [Fact]
    public void DropOlderThan_RemovesStaleEntriesOnly()
    {
        var store = new PendingActivationStore(NewContext(), Tenant);
        store.Track(Request("req-old", PimSource.EntraId, null, DateTimeOffset.UtcNow.AddHours(-25)));
        store.Track(Request("req-new", PimSource.EntraId, null, DateTimeOffset.UtcNow.AddMinutes(-5)));

        var dropped = store.DropOlderThan(TimeSpan.FromHours(24));

        Assert.Equal(1, dropped);
        Assert.Single(store.Current);
        Assert.Equal("req-new", store.Current[0].RequestId);
        Assert.Single(new PendingActivationStore(NewContext(), Tenant).Current);
    }

    [Fact]
    public void DropOlderThan_NothingStale_ReturnsZero()
    {
        var store = new PendingActivationStore(NewContext(), Tenant);
        store.Track(Request("req-new", PimSource.EntraId, null, DateTimeOffset.UtcNow));

        Assert.Equal(0, store.DropOlderThan(TimeSpan.FromHours(24)));
        Assert.Single(store.Current);
    }

    // ---- helpers ----------------------------------------------------------

    private string StorePath => Path.Combine(_dataDir, $"pending-activations-{Tenant.TenantId}.json");

    private static PendingActivationRequest Request(
        string requestId, PimSource source, string? armScope, DateTimeOffset submittedAt)
        => new(
            Source: source,
            RequestId: requestId,
            RoleName: "Owner",
            ScopeDisplay: armScope is null ? "Entra ID directory" : "Dev (sub)",
            ArmScope: armScope,
            SubmittedAt: submittedAt);

    private IPluginContext NewContext()
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<PendingActivationStoreTests>.Instance);
        ctx.DataDir.Returns(_dataDir);
        return ctx;
    }
}
