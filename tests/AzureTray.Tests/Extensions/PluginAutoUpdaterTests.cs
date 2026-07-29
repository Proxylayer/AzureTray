using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using AzureTray.Configuration;
using AzureTray.Extensions;
using AzureTray.Notifications;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using Xunit;

namespace AzureTray.Tests.Extensions;

// Unattended updates. Every refusal here has to satisfy three things at once:
// nothing is installed, the reason is logged, and the plugin the user already
// had keeps working. A gate that logs a refusal but installs anyway is worse
// than no gate at all, so each test asserts all three.
[Collection(PluginBackupTemp.Name)]
public sealed class PluginAutoUpdaterTests : IDisposable
{
    private const string PackageId = "Acme.Plugin.Foo";
    private const string InstalledBytes = "installed-v1";
    private const string UpdatedBytes = "downloaded-v2";

    private readonly string _root;
    private readonly string _pluginsDir;
    private readonly string _installedDll;

    private readonly IExtensionInstaller _installer = Substitute.For<IExtensionInstaller>();
    private readonly IPackageSecurityScanner _scanner = Substitute.For<IPackageSecurityScanner>();
    private readonly IPluginSignatureVerifier _verifier = Substitute.For<IPluginSignatureVerifier>();
    private readonly IPluginLoader _loader = Substitute.For<IPluginLoader>();
    private readonly INotifier _notifier = Substitute.For<INotifier>();
    private readonly RecordingLogger<PluginAutoUpdater> _logger = new();

    public PluginAutoUpdaterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AzureTray.Tests.AutoUpdater", Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(_root, "plugins");
        var pluginDir = Path.Combine(_pluginsDir, PackageId);
        Directory.CreateDirectory(pluginDir);
        _installedDll = Path.Combine(pluginDir, PackageId + ".dll");
        File.WriteAllText(_installedDll, InstalledBytes);
        File.WriteAllText(Path.Combine(pluginDir, "Dep.dll"), "dep-v1");

        // Defaults: clean scan, signed binary, load succeeds, install writes
        // the new bytes. Each test spoils exactly the one thing it is about.
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CleanScan());
        _verifier.Verify(Arg.Any<string>())
            .Returns(new SignatureVerdict(IsSigned: true, SignerThumbprint: "DEAD", Subject: "CN=Acme"));
        _loader.LoadedPlugins.Returns(Array.Empty<LoadedPlugin>());
        _loader.LoadOrReloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<LoadedPlugin?>(LoadedFor(_installedDll)));
        StubSuccessfulInstall();
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

    // ─── clean path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_CleanPath_InstallsAndReportsApplied()
    {
        var updater = NewUpdater();

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        Assert.Equal(PackageId, Assert.Single(applied).PackageId);
        await _installer.Received().InstallFromUrlAsync(
            PackageId, "https://nuget/foo/2.0.0.nupkg", Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Equal(UpdatedBytes, File.ReadAllText(_installedDll));
        await _loader.Received().LoadOrReloadAsync(_installedDll, Arg.Any<CancellationToken>());
        Assert.True(_logger.HasMessageContaining(LogLevel.Information, "Auto-updated"));
    }

    [Fact]
    public async Task ApplyAsync_WithNothingToDo_AppliesNothing()
    {
        var updater = NewUpdater();

        Assert.Empty(await updater.ApplyAsync(Array.Empty<PluginUpdate>(), CancellationToken.None));
        await _installer.DidNotReceiveWithAnyArgs().InstallFromUrlAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ApplyAsync_ThrowsOnNullUpdates()
        => await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await NewUpdater().ApplyAsync(null!, CancellationToken.None));

    [Fact]
    public async Task ApplyAsync_LowSeverityAdvisoriesDoNotBlockTheUpdate()
    {
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Scan(new SecurityAdvisory("GHSA-low-0001", AdvisorySeverity.Low, "Cosmetic.", null, null)));
        var updater = NewUpdater();

        Assert.Single(await updater.ApplyAsync([Update()], CancellationToken.None));
        Assert.Equal(UpdatedBytes, File.ReadAllText(_installedDll));
    }

    // ─── refusal gate 1: advisories ─────────────────────────────────────────

    [Theory]
    [InlineData(AdvisorySeverity.High)]
    [InlineData(AdvisorySeverity.Critical)]
    public async Task ApplyAsync_RefusesWhenGhsaReportsHighOrCritical(AdvisorySeverity severity)
    {
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Scan(new SecurityAdvisory("GHSA-aaaa-bbbb-cccc", severity, "Remote code execution.", null, null)));
        var updater = NewUpdater();

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        AssertRefused(applied);
        Assert.True(_logger.HasMessageContaining(LogLevel.Warning, "GHSA-aaaa-bbbb-cccc"));
    }

    [Fact]
    public async Task ApplyAsync_RefusesWhenTheScanCouldNotRun()
    {
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PackageSecurityScanResult(
                PackageId, "2.0.0", Array.Empty<SecurityAdvisory>(), ScanSucceeded: false, ScanError: "network down"));
        var updater = NewUpdater();

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        // Unattended takes the conservative reading; the interactive path is
        // allowed to proceed because it can tell the user the scan didn't run.
        AssertRefused(applied);
        Assert.True(_logger.HasMessageContaining(LogLevel.Warning, "vulnerability scan unavailable"));
    }

    [Fact]
    public async Task ApplyAsync_RefusesWhenTheScanThrows()
    {
        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("GHSA exploded"));
        var updater = NewUpdater();

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        AssertRefused(applied);
        Assert.True(_logger.HasMessageContaining(LogLevel.Warning, "vulnerability scan threw"));
    }

    // ─── refusal gate 2: trust ──────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_RefusesBeforeDownloadingWhenTheInstalledBinaryIsUnsigned()
    {
        _verifier.Verify(Arg.Any<string>()).Returns(SignatureVerdict.NotSigned);
        var updater = NewUpdater();

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        AssertRefused(applied);
        // Nothing may be fetched: the interactive path would have prompted.
        await _installer.DidNotReceiveWithAnyArgs().InstallFromUrlAsync(default!, default!, default, default);
        Assert.True(_logger.HasMessageContaining("not Authenticode-signed"));
    }

    [Fact]
    public async Task ApplyAsync_RefusesWhenSignatureVerificationThrows()
    {
        _verifier.Verify(Arg.Any<string>()).Throws(new InvalidOperationException("crypt32 said no"));
        var updater = NewUpdater();

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        AssertRefused(applied);
        await _installer.DidNotReceiveWithAnyArgs().InstallFromUrlAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ApplyAsync_UnderRequireTrustedPublisher_RefusesAnUnlistedThumbprint()
    {
        _verifier.Verify(Arg.Any<string>())
            .Returns(new SignatureVerdict(IsSigned: true, SignerThumbprint: "BEEF", Subject: "CN=Someone Else"));
        var updater = NewUpdater(new PluginOptions
        {
            TrustMode = PluginTrustMode.RequireTrustedPublisher,
            TrustedPublisherThumbprints = { "DEAD" },
        });

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        AssertRefused(applied);
        await _installer.DidNotReceiveWithAnyArgs().InstallFromUrlAsync(default!, default!, default, default);
        Assert.True(_logger.HasMessageContaining("not signed by a trusted publisher"));
    }

    [Fact]
    public async Task ApplyAsync_UnderRequireTrustedPublisher_AppliesAMatchingThumbprint()
    {
        var updater = NewUpdater(new PluginOptions
        {
            TrustMode = PluginTrustMode.RequireTrustedPublisher,
            TrustedPublisherThumbprints = { "dead" },   // case-insensitive match
        });

        Assert.Single(await updater.ApplyAsync([Update()], CancellationToken.None));
        Assert.Equal(UpdatedBytes, File.ReadAllText(_installedDll));
    }

    [Fact]
    public async Task ApplyAsync_RollsBackWhenTheDownloadedBinaryIsUnsigned()
    {
        // Signed on disk today, unsigned in the new package: the pre-download
        // preflight passes and the post-install gate has to catch it.
        _verifier.Verify(_installedDll)
            .Returns(new SignatureVerdict(IsSigned: true, SignerThumbprint: "DEAD", Subject: "CN=Acme"));
        var updater = NewUpdater();
        _installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                File.WriteAllText(_installedDll, UpdatedBytes);
                _verifier.Verify(_installedDll).Returns(SignatureVerdict.NotSigned);
                return Task.FromResult<IReadOnlyList<string>>(new[] { _installedDll });
            });

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        Assert.Empty(applied);
        Assert.Equal(InstalledBytes, File.ReadAllText(_installedDll));
        await _loader.Received().LoadOrReloadAsync(_installedDll, Arg.Any<CancellationToken>());
        Assert.True(_logger.HasMessageContaining(LogLevel.Warning, "rolled back"));
    }

    // ─── refusal gate 3: legacy layout ──────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_RefusesTheLegacyTopLevelLayout()
    {
        var legacyDll = Path.Combine(_pluginsDir, PackageId + ".dll");
        File.WriteAllText(legacyDll, InstalledBytes);
        var updater = NewUpdater();

        var applied = await updater.ApplyAsync([Update(installedDllPath: legacyDll)], CancellationToken.None);

        Assert.Empty(applied);
        Assert.Equal(InstalledBytes, File.ReadAllText(legacyDll));
        await _installer.DidNotReceiveWithAnyArgs().InstallFromUrlAsync(default!, default!, default, default);
        // The layout check comes first — there is no point scanning a package
        // we already know we won't install.
        await _scanner.DidNotReceiveWithAnyArgs().ScanAsync(default!, default!, default);
        Assert.True(_logger.HasMessageContaining(LogLevel.Warning, "legacy top-level layout"));
    }

    // ─── refusal gate 4: install failed / nothing loadable ──────────────────

    [Fact]
    public async Task ApplyAsync_RestoresTheSnapshotWhenTheInstallThrows()
    {
        _installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => FailMidInstall());
        var updater = NewUpdater();

        // Half-written folder, exactly what a mid-download failure leaves.
        Task<IReadOnlyList<string>> FailMidInstall()
        {
            File.WriteAllText(_installedDll, "corrupt-half-write");
            File.Delete(Path.Combine(_pluginsDir, PackageId, "Dep.dll"));
            throw new IOException("connection reset");
        }

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        Assert.Empty(applied);
        Assert.Equal(InstalledBytes, File.ReadAllText(_installedDll));
        Assert.Equal("dep-v1", File.ReadAllText(Path.Combine(_pluginsDir, PackageId, "Dep.dll")));
        await _loader.Received().LoadOrReloadAsync(_installedDll, Arg.Any<CancellationToken>());
        Assert.True(_logger.HasMessageContaining(LogLevel.Warning, "rolling back"));
    }

    [Fact]
    public async Task ApplyAsync_RestoresTheSnapshotWhenNothingLoadsFromTheNewPackage()
    {
        _loader.LoadOrReloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LoadedPlugin?>(null));
        var updater = NewUpdater();

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        Assert.Empty(applied);
        Assert.Equal(InstalledBytes, File.ReadAllText(_installedDll));
        Assert.True(_logger.HasMessageContaining(LogLevel.Warning, "no loadable plugin"));
        // The previous DLL is reloaded so the user keeps a working plugin.
        await _loader.Received().LoadOrReloadAsync(_installedDll, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApplyAsync_RollsBackWhenTheHotLoadThrows()
    {
        _loader.LoadOrReloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => FailLoad());
        var updater = NewUpdater();

        static Task<LoadedPlugin?> FailLoad()
            => throw new BadImageFormatException("not a managed assembly");

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        Assert.Empty(applied);
        Assert.Equal(InstalledBytes, File.ReadAllText(_installedDll));
    }

    [Fact]
    public async Task ApplyAsync_RefusesWhenTheInstallProducedNoDlls()
    {
        _installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));
        var updater = NewUpdater();

        var applied = await updater.ApplyAsync([Update()], CancellationToken.None);

        Assert.Empty(applied);
        Assert.Equal(InstalledBytes, File.ReadAllText(_installedDll));
    }

    // ─── mixed batches ──────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_ARefusedUpdateDoesNotStopTheRest()
    {
        var otherDir = Path.Combine(_pluginsDir, "Acme.Plugin.Bar");
        Directory.CreateDirectory(otherDir);
        var otherDll = Path.Combine(otherDir, "Acme.Plugin.Bar.dll");
        File.WriteAllText(otherDll, InstalledBytes);

        // Foo has a Critical advisory; Bar is clean.
        _scanner.ScanAsync(PackageId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Scan(new SecurityAdvisory("GHSA-dead-beef-cafe", AdvisorySeverity.Critical, "RCE.", null, null)));
        _scanner.ScanAsync("Acme.Plugin.Bar", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(CleanScan());
        _installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                File.WriteAllText(otherDll, UpdatedBytes);
                return Task.FromResult<IReadOnlyList<string>>(new[] { otherDll });
            });
        _loader.LoadOrReloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<LoadedPlugin?>(LoadedFor(otherDll)));

        var updater = NewUpdater();
        var applied = await updater.ApplyAsync(
            [Update(), Update(packageId: "Acme.Plugin.Bar", installedDllPath: otherDll)],
            CancellationToken.None);

        Assert.Equal("Acme.Plugin.Bar", Assert.Single(applied).PackageId);
        Assert.Equal(InstalledBytes, File.ReadAllText(_installedDll));
        Assert.Equal(UpdatedBytes, File.ReadAllText(otherDll));
    }

    [Fact]
    public async Task ApplyAsync_HonoursCancellationBeforeTouchingAnything()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var updater = NewUpdater();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await updater.ApplyAsync([Update()], cts.Token));

        await _installer.DidNotReceiveWithAnyArgs().InstallFromUrlAsync(default!, default!, default, default);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    // Every refusal shares the same three obligations.
    private void AssertRefused(IReadOnlyList<PluginUpdate> applied)
    {
        Assert.Empty(applied);
        Assert.Equal(InstalledBytes, File.ReadAllText(_installedDll));
        Assert.Equal("dep-v1", File.ReadAllText(Path.Combine(_pluginsDir, PackageId, "Dep.dll")));
        Assert.Contains(_logger.Entries, e => e.Level >= LogLevel.Information && e.Message.Contains("declined", StringComparison.OrdinalIgnoreCase));
    }

    private void StubSuccessfulInstall()
        => _installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                File.WriteAllText(_installedDll, UpdatedBytes);
                return Task.FromResult<IReadOnlyList<string>>(new[] { _installedDll });
            });

    private static PackageSecurityScanResult CleanScan()
        => new(PackageId, "2.0.0", Array.Empty<SecurityAdvisory>(), ScanSucceeded: true, ScanError: null);

    private static PackageSecurityScanResult Scan(params SecurityAdvisory[] advisories)
        => new(PackageId, "2.0.0", advisories, ScanSucceeded: true, ScanError: null);

    private static LoadedPlugin LoadedFor(string dllPath)
    {
        var plugin = Substitute.For<ITrayPlugin>();
        plugin.Id.Returns("com.acme.foo");
        plugin.Version.Returns("2.0.0");
        plugin.DisplayName.Returns("Foo Plugin");
        return new LoadedPlugin(plugin, dllPath, SignatureVerdict.NotSigned);
    }

    private PluginUpdate Update(string? packageId = null, string? installedDllPath = null)
    {
        packageId ??= PackageId;
        var latest = new NuGetPluginVersion(
            "2.0.0", null, null, $"https://nuget/{(packageId == PackageId ? "foo" : "bar")}/2.0.0.nupkg", null);
        var entry = new NuGetPluginEntry(
            Id: packageId,
            DisplayName: packageId,
            Publisher: "Acme",
            PublisherUrl: null,
            Description: null,
            Tags: ["proxylayer.azuretray-plugin"],
            SourceRepo: null,
            IconUrl: null,
            NuGetPackageId: packageId,
            Versions: [latest]);

        return new PluginUpdate(
            PackageId: packageId,
            PluginId: "com.acme.foo",
            InstalledVersion: "1.0.0",
            InstalledDllPath: installedDllPath ?? _installedDll,
            Entry: entry,
            Latest: latest);
    }

    private PluginAutoUpdater NewUpdater(PluginOptions? options = null)
        => new(
            _installer,
            _scanner,
            _verifier,
            _loader,
            new PluginUpdateNotifier(_notifier, NullLogger<PluginUpdateNotifier>.Instance),
            Options.Create(options ?? new PluginOptions()),
            _logger);
}
