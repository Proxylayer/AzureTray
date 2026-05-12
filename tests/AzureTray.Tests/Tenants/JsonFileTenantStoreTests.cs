using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray;
using AzureTray.Models;
using AzureTray.Tenants;
using Xunit;

namespace AzureTray.Tests.Tenants;

public sealed class JsonFileTenantStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "AzureTray.Tests.Tenants",
        Guid.NewGuid().ToString("N"));

    public JsonFileTenantStoreTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    [Fact]
    public void GetAll_WhenFileDoesNotExist_ReturnsEmpty()
    {
        var store = NewStore();
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task AddOrUpdate_PersistsAcrossInstances()
    {
        var paths = Paths();
        var first = new JsonFileTenantStore(paths, NullLogger<JsonFileTenantStore>.Instance);

        await first.AddOrUpdateAsync(new Tenant("tenant-1", "Contoso", "client-1"), CancellationToken.None);
        await first.AddOrUpdateAsync(new Tenant("tenant-2", "Fabrikam", null), CancellationToken.None);

        var second = new JsonFileTenantStore(paths, NullLogger<JsonFileTenantStore>.Instance);
        var all = second.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, t => t.TenantId == "tenant-1" && t.DisplayName == "Contoso" && t.ClientId == "client-1");
        Assert.Contains(all, t => t.TenantId == "tenant-2" && t.ClientId == null);
    }

    [Fact]
    public async Task AddOrUpdate_ReplacesExistingTenantById()
    {
        var store = NewStore();

        await store.AddOrUpdateAsync(new Tenant("tenant-1", "First", "client-a"), CancellationToken.None);
        await store.AddOrUpdateAsync(new Tenant("tenant-1", "First Renamed", "client-b"), CancellationToken.None);

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("First Renamed", all[0].DisplayName);
        Assert.Equal("client-b", all[0].ClientId);
    }

    [Fact]
    public async Task Remove_DropsTenantAndPersists()
    {
        var paths = Paths();
        var store = new JsonFileTenantStore(paths, NullLogger<JsonFileTenantStore>.Instance);
        await store.AddOrUpdateAsync(new Tenant("tenant-1", "Contoso", null), CancellationToken.None);

        await store.RemoveAsync("tenant-1", CancellationToken.None);

        Assert.Empty(store.GetAll());
        Assert.Empty(new JsonFileTenantStore(paths, NullLogger<JsonFileTenantStore>.Instance).GetAll());
    }

    [Fact]
    public void Load_OnMalformedFile_LogsAndStartsEmpty()
    {
        var paths = Paths();
        File.WriteAllText(paths.TenantStoreFilePath, "{ not valid json");

        var store = new JsonFileTenantStore(paths, NullLogger<JsonFileTenantStore>.Instance);

        Assert.Empty(store.GetAll());
    }

    [Fact]
    public async Task FindByTenantId_IsCaseInsensitive()
    {
        var store = NewStore();
        await store.AddOrUpdateAsync(new Tenant("ABCD-1234", "Test", null), CancellationToken.None);

        Assert.NotNull(store.FindByTenantId("abcd-1234"));
    }

    private IAppPaths Paths()
    {
        var paths = Substitute.For<IAppPaths>();
        paths.TenantStoreFilePath.Returns(Path.Combine(_tempDir, "tenants.json"));
        return paths;
    }

    private JsonFileTenantStore NewStore()
        => new(Paths(), NullLogger<JsonFileTenantStore>.Instance);
}
