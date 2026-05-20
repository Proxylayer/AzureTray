using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AzureTray;
using AzureTray.AzureCloud;
using AzureTray.Configuration;
using AzureTray.Extensions;
using AzureTray.Models;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using AzureTray.Tenants;
using Xunit;

namespace AzureTray.Tests.Plugins;

public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "AzureTray.Tests.Plugins",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task LoadAllAsync_WhenPluginsDirectoryDoesNotExist_DoesNothing()
    {
        var loader = BuildLoader(pluginsDir: Path.Combine(_tempRoot, "absent"));

        await loader.LoadAllAsync(CancellationToken.None);

        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public async Task LoadAllAsync_WhenPluginsDirectoryIsEmpty_DoesNothing()
    {
        var pluginsDir = Path.Combine(_tempRoot, "plugins");
        Directory.CreateDirectory(pluginsDir);
        var loader = BuildLoader(pluginsDir);

        await loader.LoadAllAsync(CancellationToken.None);

        Assert.Empty(loader.LoadedPlugins);
    }

    [Fact]
    public async Task LoadAllAsync_RejectsRandomFile_WithRequireSigned()
    {
        var pluginsDir = Path.Combine(_tempRoot, "plugins");
        Directory.CreateDirectory(pluginsDir);
        var fake = Path.Combine(pluginsDir, "fake.dll");
        File.WriteAllBytes(fake, new byte[] { 0x01, 0x02, 0x03 });

        var loader = BuildLoader(pluginsDir, PluginTrustMode.RequireSigned);

        await loader.LoadAllAsync(CancellationToken.None);

        Assert.Empty(loader.LoadedPlugins);
    }

    private static PluginLoader BuildLoader(string pluginsDir, PluginTrustMode trustMode = PluginTrustMode.RequireSigned)
    {
        var paths = Substitute.For<IAppPaths>();
        paths.PluginsDir.Returns(pluginsDir);

        var options = Options.Create(new PluginOptions { TrustMode = trustMode });
        var verifier = new AuthenticodePluginSignatureVerifier(
            NullLogger<AuthenticodePluginSignatureVerifier>.Instance);

        var http = Substitute.For<IPluginHttpClientCore>();
        var notifier = Substitute.For<INotifier>();
        var clipboard = Substitute.For<IClipboard>();
        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAll().Returns(System.Array.Empty<Tenant>());
        var cloud = Substitute.For<IAzureCloudConfig>();
        cloud.GraphScope.Returns("https://graph.microsoft.com/.default");
        cloud.ArmScope.Returns("https://management.azure.com/.default");
        var installer = Substitute.For<IExtensionInstaller>();
        var readiness = new TenantReadinessTracker();
        var configStore = Substitute.For<IPluginConfigStore>();
        configStore.IsTenantEnabledFor(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        configStore.GetOptions(Arg.Any<string>()).Returns(new System.Collections.Generic.Dictionary<string, object?>());

        return new PluginLoader(paths, verifier, options, http, notifier, clipboard, tenantStore, cloud, installer, readiness, configStore, NullLoggerFactory.Instance);
    }
}
