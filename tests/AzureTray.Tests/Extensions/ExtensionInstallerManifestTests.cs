using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray;
using AzureTray.Extensions;
using Xunit;

namespace AzureTray.Tests.Extensions;

// The install manifest half of ExtensionInstaller. Version detection reads the
// package's OWN <metadata>: a <dependency> in the same .nuspec carries an
// id/version pair too, and picking one of those up would record a version that
// has nothing to do with the plugin — permanently mis-reporting updates.
public sealed class ExtensionInstallerManifestTests : IDisposable
{
    private readonly string _root;
    private readonly string _pluginsDir;
    private readonly string _sourceDir;
    private readonly IAppPaths _paths;

    public ExtensionInstallerManifestTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AzureTray.Tests.InstallerManifest", Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(_root, "plugins");
        _sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(_pluginsDir);
        Directory.CreateDirectory(_sourceDir);

        _paths = Substitute.For<IAppPaths>();
        _paths.PluginsDir.Returns(_pluginsDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort.
        }
    }

    [Fact]
    public async Task InstallFromFileAsync_WritesManifestWithTheVersionFromPackageMetadata()
    {
        var nupkg = WriteNupkg("Acme.Plugin.Foo", Nuspec("Acme.Plugin.Foo", "1.4.2"));
        var installer = NewInstaller();

        await installer.InstallFromFileAsync(nupkg, CancellationToken.None);

        var manifest = ReadManifest("Acme.Plugin.Foo");
        Assert.NotNull(manifest);
        Assert.Equal("Acme.Plugin.Foo", manifest!.PackageId);
        Assert.Equal("1.4.2", manifest.Version);
        // Local-file install: no feed URL to record.
        Assert.Null(manifest.SourceUrl);
    }

    // Regression guard: the .nuspec declares a dependency with a DIFFERENT id
    // and version. The manifest must carry the package's own identity.
    [Fact]
    public async Task InstallFromFileAsync_DependencyIdAndVersionDoNotPoisonTheManifest()
    {
        var nuspec = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Acme.Plugin.Foo</id>
                <version>1.4.2</version>
                <description>Test package.</description>
                <authors>Test</authors>
                <dependencies>
                  <group targetFramework="net8.0">
                    <dependency id="Some.Other.Package" version="9.9.9" exclude="Build,Analyzers" />
                    <dependency id="Yet.Another" version="0.0.1" />
                  </group>
                </dependencies>
              </metadata>
            </package>
            """;
        var nupkg = WriteNupkg("Acme.Plugin.Foo", nuspec);
        var installer = NewInstaller();

        await installer.InstallFromFileAsync(nupkg, CancellationToken.None);

        var manifest = ReadManifest("Acme.Plugin.Foo");
        Assert.NotNull(manifest);
        Assert.Equal("Acme.Plugin.Foo", manifest!.PackageId);
        Assert.Equal("1.4.2", manifest.Version);
    }

    // Same guard against a descendant walk, with the dependency identity in
    // child ELEMENTS rather than attributes — the shape a bare
    // Descendants("version") walk would happily pick up.
    [Fact]
    public async Task InstallFromFileAsync_NestedIdAndVersionElementsDoNotPoisonTheManifest()
    {
        var nuspec = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Acme.Plugin.Foo</id>
                <version>1.4.2</version>
                <description>Test package.</description>
                <authors>Test</authors>
                <dependencies>
                  <dependency>
                    <id>Some.Other.Package</id>
                    <version>9.9.9</version>
                  </dependency>
                </dependencies>
              </metadata>
            </package>
            """;
        var nupkg = WriteNupkg("Acme.Plugin.Foo", nuspec);
        var installer = NewInstaller();

        await installer.InstallFromFileAsync(nupkg, CancellationToken.None);

        var manifest = ReadManifest("Acme.Plugin.Foo");
        Assert.NotNull(manifest);
        Assert.Equal("1.4.2", manifest!.Version);
    }

    [Fact]
    public async Task InstallFromFileAsync_WithoutAVersion_SkipsTheManifestButStillInstalls()
    {
        var nuspec = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Acme.Plugin.NoVersion</id>
                <description>Test package.</description>
                <authors>Test</authors>
              </metadata>
            </package>
            """;
        var nupkg = WriteNupkg("Acme.Plugin.NoVersion", nuspec);
        var logger = new RecordingLogger<ExtensionInstaller>();
        var installer = NewInstaller(logger);

        var installed = await installer.InstallFromFileAsync(nupkg, CancellationToken.None);

        // The install itself must not fail over a missing manifest.
        Assert.Single(installed);
        Assert.True(File.Exists(installed[0]));
        Assert.Null(ReadManifest("Acme.Plugin.NoVersion"));
        Assert.False(
            File.Exists(Path.Combine(_pluginsDir, "Acme.Plugin.NoVersion", PluginManifestStore.FileName)),
            "no manifest file should be written when the .nuspec has no <version>");
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("no <version>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallFromUrlAsync_RecordsTheDownloadedPackagesOwnVersionAndSourceUrl()
    {
        const string url = "https://api.nuget.org/v3-flatcontainer/acme.plugin.foo/2.0.0/acme.plugin.foo.2.0.0.nupkg";
        var bytes = await File.ReadAllBytesAsync(
            WriteNupkg("Acme.Plugin.Foo", Nuspec("Acme.Plugin.Foo", "2.0.0")));
        var installer = NewInstaller(httpFactory: StubHttpFactory(bytes));

        await installer.InstallFromUrlAsync("Acme.Plugin.Foo", url, checksumSha256: null, CancellationToken.None);

        var manifest = ReadManifest("Acme.Plugin.Foo");
        Assert.NotNull(manifest);
        Assert.Equal("2.0.0", manifest!.Version);
        Assert.Equal(url, manifest.SourceUrl);
    }

    // The caller's idea of the version is irrelevant — the package's own
    // metadata describes what actually landed on disk.
    [Fact]
    public async Task InstallFromUrlAsync_IgnoresTheVersionInTheUrl()
    {
        const string url = "https://api.nuget.org/v3-flatcontainer/acme.plugin.foo/9.9.9/acme.plugin.foo.9.9.9.nupkg";
        var bytes = await File.ReadAllBytesAsync(
            WriteNupkg("Acme.Plugin.Foo", Nuspec("Acme.Plugin.Foo", "2.0.0")));
        var installer = NewInstaller(httpFactory: StubHttpFactory(bytes));

        await installer.InstallFromUrlAsync("Acme.Plugin.Foo", url, checksumSha256: null, CancellationToken.None);

        Assert.Equal("2.0.0", ReadManifest("Acme.Plugin.Foo")!.Version);
    }

    private static string Nuspec(string packageId, string version) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{{packageId}}</id>
            <version>{{version}}</version>
            <description>Test package.</description>
            <authors>Test</authors>
          </metadata>
        </package>
        """;

    private string WriteNupkg(string packageId, string nuspec)
    {
        var nupkgPath = Path.Combine(_sourceDir, $"{packageId}.nupkg");
        using var fs = File.Create(nupkgPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        using (var entry = zip.CreateEntry($"{packageId}.nuspec").Open())
        {
            var bytes = Encoding.UTF8.GetBytes(nuspec);
            entry.Write(bytes, 0, bytes.Length);
        }
        using (var entry = zip.CreateEntry($"lib/net8.0/{packageId}.dll").Open())
        {
            entry.Write(new byte[] { 0x4D, 0x5A, 0x01 }, 0, 3);
        }

        return nupkgPath;
    }

    private InstalledPluginManifest? ReadManifest(string packageId)
        => new PluginManifestStore(_paths, NullLogger<PluginManifestStore>.Instance).TryRead(packageId);

    private static IHttpClientFactory StubHttpFactory(byte[] payload)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(ExtensionInstaller.HttpClientName)
            .Returns(_ => new HttpClient(new StaticBytesHandler(payload)));
        return factory;
    }

    private ExtensionInstaller NewInstaller(
        ILogger<ExtensionInstaller>? logger = null,
        IHttpClientFactory? httpFactory = null)
        => new(
            _paths,
            httpFactory ?? Substitute.For<IHttpClientFactory>(),
            new PluginManifestStore(_paths, NullLogger<PluginManifestStore>.Instance),
            logger ?? NullLogger<ExtensionInstaller>.Instance);

    private sealed class StaticBytesHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public StaticBytesHandler(byte[] payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload),
            });
    }
}
