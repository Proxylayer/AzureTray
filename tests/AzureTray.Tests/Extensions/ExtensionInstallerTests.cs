using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray;
using AzureTray.Extensions;
using Xunit;

namespace AzureTray.Tests.Extensions;

public sealed class ExtensionInstallerTests : IDisposable
{
    private readonly string _pluginsDir;
    private readonly string _sourceDir;

    public ExtensionInstallerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "AzureTray.Tests.Extensions", Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(root, "plugins");
        _sourceDir = Path.Combine(root, "source");
        Directory.CreateDirectory(_pluginsDir);
        Directory.CreateDirectory(_sourceDir);
    }

    public void Dispose()
    {
        try
        {
            var root = Directory.GetParent(_pluginsDir)?.FullName;
            if (root is not null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    [Fact]
    public async Task InstallFromFileAsync_ExtractsPluginDllsIntoIdSubfolder()
    {
        var packageId = "Acme.Plugin.Sample";
        var nupkgPath = WriteSampleNupkg(
            packageId,
            new[]
            {
                ("lib/net8.0/Acme.Plugin.Sample.dll", new byte[] { 0x4D, 0x5A, 0xAA }),
                ("lib/net8.0/Newtonsoft.Json.dll", new byte[] { 0x4D, 0x5A, 0xBB }),
            });
        var installer = NewInstaller();

        var installed = await installer.InstallFromFileAsync(nupkgPath, CancellationToken.None);

        var expectedDir = Path.Combine(_pluginsDir, packageId);
        Assert.True(Directory.Exists(expectedDir));
        Assert.Equal(2, installed.Count);
        Assert.Contains(installed, p => Path.GetFileName(p) == "Acme.Plugin.Sample.dll");
        Assert.Contains(installed, p => Path.GetFileName(p) == "Newtonsoft.Json.dll");
    }

    [Fact]
    public async Task InstallFromFileAsync_PrefersNet80OverNetstandard()
    {
        var packageId = "Acme.Plugin.MultiTfm";
        var nupkgPath = WriteSampleNupkg(
            packageId,
            new[]
            {
                ("lib/netstandard2.0/Acme.Plugin.MultiTfm.dll", new byte[] { 0x99 }),
                ("lib/net8.0/Acme.Plugin.MultiTfm.dll", new byte[] { 0x4D, 0x5A }),
            });
        var installer = NewInstaller();

        var installed = await installer.InstallFromFileAsync(nupkgPath, CancellationToken.None);

        var path = Assert.Single(installed);
        Assert.Equal(new byte[] { 0x4D, 0x5A }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InstallFromFileAsync_RejectsNonNupkg()
    {
        var source = Path.Combine(_sourceDir, "NotAPackage.zip");
        await File.WriteAllBytesAsync(source, new byte[] { 0x50, 0x4B });
        var installer = NewInstaller();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await installer.InstallFromFileAsync(source, CancellationToken.None));
    }

    [Fact]
    public async Task InstallFromFileAsync_FailsWhenNuspecMissing()
    {
        var nupkgPath = Path.Combine(_sourceDir, "broken.nupkg");
        using (var fs = File.Create(nupkgPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var entry = zip.CreateEntry("lib/net8.0/Some.dll").Open();
            entry.Write(new byte[] { 0x4D, 0x5A }, 0, 2);
        }
        var installer = NewInstaller();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await installer.InstallFromFileAsync(nupkgPath, CancellationToken.None));
    }

    [Fact]
    public async Task TryDeleteAsync_RemovesEntireSubfolderForNupkgInstalls()
    {
        var packageId = "Acme.Plugin.Subfolder";
        var nupkgPath = WriteSampleNupkg(
            packageId,
            new[]
            {
                ("lib/net8.0/Acme.Plugin.Subfolder.dll", new byte[] { 0x4D, 0x5A }),
                ("lib/net8.0/SomeDep.dll", new byte[] { 0x4D, 0x5A }),
            });
        var installer = NewInstaller();
        var installed = await installer.InstallFromFileAsync(nupkgPath, CancellationToken.None);
        var primaryDll = installed.First(p => Path.GetFileName(p) == "Acme.Plugin.Subfolder.dll");

        var deleted = await installer.TryDeleteAsync(primaryDll, CancellationToken.None);

        Assert.True(deleted);
        Assert.False(Directory.Exists(Path.Combine(_pluginsDir, packageId)));
    }

    [Fact]
    public async Task TryDeleteAsync_DeletesTopLevelLegacyDll()
    {
        var dll = Path.Combine(_pluginsDir, "Legacy.dll");
        await File.WriteAllBytesAsync(dll, new byte[] { 0x00 });
        var installer = NewInstaller();

        var deleted = await installer.TryDeleteAsync(dll, CancellationToken.None);

        Assert.True(deleted);
        Assert.False(File.Exists(dll));
    }

    [Fact]
    public async Task TryDeleteAsync_RejectsPathsOutsidePluginsFolder()
    {
        var outside = Path.Combine(_sourceDir, "Stray.dll");
        await File.WriteAllBytesAsync(outside, new byte[] { 0x00 });
        var installer = NewInstaller();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await installer.TryDeleteAsync(outside, CancellationToken.None));
    }

    [Fact]
    public void ListInstalledDlls_SurfacesPrimaryDllFromEachSubfolderPlusLegacyTopLevel()
    {
        // Subfolder install: plugins/Foo/Foo.dll + plugins/Foo/SomeDep.dll (dep hidden).
        var fooDir = Path.Combine(_pluginsDir, "Foo");
        Directory.CreateDirectory(fooDir);
        File.WriteAllBytes(Path.Combine(fooDir, "Foo.dll"), new byte[] { 0x00 });
        File.WriteAllBytes(Path.Combine(fooDir, "SomeDep.dll"), new byte[] { 0x00 });

        // Legacy top-level install: plugins/Bar.dll.
        File.WriteAllBytes(Path.Combine(_pluginsDir, "Bar.dll"), new byte[] { 0x00 });

        var installer = NewInstaller();

        var dlls = installer.ListInstalledDlls()
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("Foo.dll", dlls);
        Assert.Contains("Bar.dll", dlls);
        Assert.DoesNotContain("SomeDep.dll", dlls);
    }

    [Fact]
    public void ProcessPendingUninstalls_WipesSubfolderWithSentinel()
    {
        var fooDir = Path.Combine(_pluginsDir, "Foo");
        Directory.CreateDirectory(fooDir);
        File.WriteAllBytes(Path.Combine(fooDir, "Foo.dll"), new byte[] { 0x00 });
        File.WriteAllText(Path.Combine(fooDir, "uninstall"), string.Empty);
        var installer = NewInstaller();

        installer.ProcessPendingUninstalls();

        Assert.False(Directory.Exists(fooDir));
    }

    [Fact]
    public void ProcessPendingUninstalls_DeletesLegacyTopLevelDll()
    {
        var dll = Path.Combine(_pluginsDir, "Bar.dll");
        File.WriteAllBytes(dll, new byte[] { 0x00 });
        File.WriteAllText(dll + ".uninstall", string.Empty);
        var installer = NewInstaller();

        installer.ProcessPendingUninstalls();

        Assert.False(File.Exists(dll));
        Assert.False(File.Exists(dll + ".uninstall"));
    }

    private string WriteSampleNupkg(string packageId, (string path, byte[] bytes)[] entries)
    {
        var nupkgPath = Path.Combine(_sourceDir, $"{packageId}.1.0.0.nupkg");
        using var fs = File.Create(nupkgPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var nuspec = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{{packageId}}</id>
                <version>1.0.0</version>
                <description>Test package.</description>
                <authors>Test</authors>
              </metadata>
            </package>
            """;
        using (var nuspecEntry = zip.CreateEntry($"{packageId}.nuspec").Open())
        {
            var bytes = Encoding.UTF8.GetBytes(nuspec);
            nuspecEntry.Write(bytes, 0, bytes.Length);
        }

        foreach (var (path, bytes) in entries)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(bytes, 0, bytes.Length);
        }

        return nupkgPath;
    }

    private ExtensionInstaller NewInstaller()
    {
        var paths = Substitute.For<IAppPaths>();
        paths.PluginsDir.Returns(_pluginsDir);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        return new ExtensionInstaller(paths, httpFactory, NullLogger<ExtensionInstaller>.Instance);
    }
}
