using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.Configuration;
using AzureTray.Notifications;
using AzureTray.Plugins;

namespace AzureTray.Extensions;

// Applies plugin updates without a user present. Only ever reached when the
// user has explicitly enabled auto-update (default off).
//
// The hard rule is that nothing which would normally ask the user a question
// may be answered on their behalf. Concretely, an update is REFUSED and left
// for a manual click when:
//
//   1. GHSA reports a High or Critical advisory for the new version. (Checked
//      before anything is downloaded.)
//   2. Installing it would need an unsigned-plugin trust decision — i.e. the
//      version currently on disk is not Authenticode-signed, so the new one
//      almost certainly isn't either and the interactive path would prompt.
//      Also refused if the new binary turns out to be unsigned/untrusted
//      after all, in which case the pre-update snapshot is restored.
//   3. The plugin was installed in the legacy top-level layout
//      (plugins/<id>.dll), where an update can't cleanly replace the old file.
//   4. Nothing loaded from the new package, or the install threw — the
//      snapshot is restored so the user keeps the version that worked.
//
// Refusals are logged with the reason and reported to the user through the
// ordinary "updates available" toast, so auto-update declining is visible
// rather than silent.
internal sealed class PluginAutoUpdater
{
    private readonly IExtensionInstaller _installer;
    private readonly IPackageSecurityScanner _scanner;
    private readonly IPluginSignatureVerifier _signatureVerifier;
    private readonly IPluginLoader _loader;
    private readonly PluginUpdateNotifier _notifier;
    private readonly PluginOptions _pluginOptions;
    private readonly ILogger<PluginAutoUpdater> _logger;

    public PluginAutoUpdater(
        IExtensionInstaller installer,
        IPackageSecurityScanner scanner,
        IPluginSignatureVerifier signatureVerifier,
        IPluginLoader loader,
        PluginUpdateNotifier notifier,
        IOptions<PluginOptions> pluginOptions,
        ILogger<PluginAutoUpdater> logger)
    {
        ArgumentNullException.ThrowIfNull(pluginOptions);

        _installer = installer;
        _scanner = scanner;
        _signatureVerifier = signatureVerifier;
        _loader = loader;
        _notifier = notifier;
        _pluginOptions = pluginOptions.Value;
        _logger = logger;
    }

    // Tries to apply every update in `updates`. Returns the ones actually
    // applied; everything else is reported back through the available-updates
    // toast for a manual decision.
    public async Task<IReadOnlyList<PluginUpdate>> ApplyAsync(
        IReadOnlyList<PluginUpdate> updates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var applied = new List<PluginUpdate>();
        var declined = new List<PluginUpdate>();

        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await TryApplyAsync(update, cancellationToken).ConfigureAwait(false))
            {
                applied.Add(update);
            }
            else
            {
                declined.Add(update);
            }
        }

        // Both toasts are non-blocking: an ActionRequest sits on screen until
        // the user deals with it, and the poll loop can't wait for that.
        _notifier.ShowUpdatesApplied(applied);
        _notifier.ShowUpdatesAvailable(declined);

        return applied;
    }

    private async Task<bool> TryApplyAsync(PluginUpdate update, CancellationToken cancellationToken)
    {
        if (!IsPerPluginFolderLayout(update))
        {
            _logger.LogWarning(
                "Auto-update declined for {PackageId} {Version}: it is installed in the legacy top-level layout ({Path}), which an in-place update can't replace cleanly. Update it manually from Settings.",
                update.PackageId, update.LatestVersion, update.InstalledDllPath);
            return false;
        }

        if (!await IsAdvisoryCleanAsync(update, cancellationToken).ConfigureAwait(false)) return false;
        if (!IsTrustPreflightClean(update)) return false;

        using var backup = PluginFolderBackup.TryCreate(update.InstalledDllPath, update.PackageId, _logger);

        IReadOnlyList<string> installed;
        try
        {
            installed = await _installer.InstallFromUrlAsync(
                update.PackageId,
                update.DownloadUrl,
                update.Latest.ChecksumSha256,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Auto-update of {PackageId} to {Version} failed while installing; rolling back to {InstalledVersion}.",
                update.PackageId, update.LatestVersion, update.InstalledVersion);
            backup?.TryRestore();
            await ReloadAsync(update.InstalledDllPath, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (!IsTrustedAfterInstall(update, installed))
        {
            // The new binary would have triggered the unsigned prompt (or is
            // rejected by RequireTrustedPublisher). Put the previous version
            // back rather than leaving unvetted bytes on disk.
            backup?.TryRestore();
            await ReloadAsync(update.InstalledDllPath, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var loadedCount = 0;
        foreach (var dll in installed)
        {
            if (await ReloadAsync(dll, cancellationToken).ConfigureAwait(false)) loadedCount++;
        }

        if (loadedCount == 0)
        {
            _logger.LogWarning(
                "Auto-update of {PackageId} to {Version} produced no loadable plugin; rolling back to {InstalledVersion}.",
                update.PackageId, update.LatestVersion, update.InstalledVersion);
            backup?.TryRestore();
            await ReloadAsync(update.InstalledDllPath, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _logger.LogInformation(
            "Auto-updated {PackageId} {InstalledVersion} → {LatestVersion} ({Count} assembly/assemblies loaded).",
            update.PackageId, update.InstalledVersion, update.LatestVersion, loadedCount);
        return true;
    }

    private static bool IsPerPluginFolderLayout(PluginUpdate update)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(update.InstalledDllPath)) ?? string.Empty);
        return string.Equals(folder, update.PackageId, StringComparison.OrdinalIgnoreCase);
    }

    // Refusal rule 1. A failed scan is NOT treated as clean: unattended
    // installs get the conservative reading, unlike the interactive path where
    // the user is told the scan didn't run and can decide.
    private async Task<bool> IsAdvisoryCleanAsync(PluginUpdate update, CancellationToken cancellationToken)
    {
        PackageSecurityScanResult scan;
        try
        {
            scan = await _scanner.ScanAsync(update.PackageId, update.LatestVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Auto-update declined for {PackageId} {Version}: the vulnerability scan threw, and an unattended install won't proceed on an unknown security state.",
                update.PackageId, update.LatestVersion);
            return false;
        }

        if (!scan.ScanSucceeded)
        {
            _logger.LogWarning(
                "Auto-update declined for {PackageId} {Version}: vulnerability scan unavailable ({Error}). Left for a manual install, where the missing scan is shown in the prompt.",
                update.PackageId, update.LatestVersion, scan.ScanError ?? "lookup error");
            return false;
        }

        if (scan.HasCriticalOrHigh)
        {
            var ids = string.Join(", ", scan.Advisories
                .Where(a => a.Severity is AdvisorySeverity.Critical or AdvisorySeverity.High)
                .Select(a => a.Id));
            _logger.LogWarning(
                "Auto-update declined for {PackageId} {Version}: GHSA reports High/Critical advisor(y/ies) {Advisories}. This needs a human decision, so it is left for a manual install.",
                update.PackageId, update.LatestVersion, ids);
            return false;
        }

        if (scan.HasAny)
        {
            _logger.LogInformation(
                "{PackageId} {Version}: {Count} lower-severity advisor(y/ies) — auto-update proceeding.",
                update.PackageId, update.LatestVersion, scan.Advisories.Count);
        }

        return true;
    }

    // Refusal rule 2, before download: if what's installed today isn't signed,
    // the interactive path would prompt for the new version too.
    private bool IsTrustPreflightClean(PluginUpdate update)
    {
        var verdict = Verify(update.InstalledDllPath);

        if (_pluginOptions.TrustMode == PluginTrustMode.RequireTrustedPublisher)
        {
            // Policy mode never prompts, so unattended is legitimate — but only
            // when the installed version already satisfies the policy. If it
            // doesn't, the plugin shouldn't be running at all and an update
            // won't fix that.
            if (IsTrustedPublisher(verdict)) return true;

            _logger.LogWarning(
                "Auto-update declined for {PackageId} {Version}: the installed binary is not signed by a trusted publisher, so the policy would reject the update too.",
                update.PackageId, update.LatestVersion);
            return false;
        }

        if (verdict.IsSigned) return true;

        _logger.LogInformation(
            "Auto-update declined for {PackageId} {Version}: the installed binary is not Authenticode-signed, so installing the new version would need the unsigned-plugin trust prompt. Notifying instead so you can approve it in Settings.",
            update.PackageId, update.LatestVersion);
        return false;
    }

    // Refusal rule 2, after download: the freshly written primary DLL must
    // clear the same bar the interactive gate applies without asking anyone.
    private bool IsTrustedAfterInstall(PluginUpdate update, IReadOnlyList<string> installedDlls)
    {
        if (installedDlls.Count == 0) return false;

        var primary = installedDlls.FirstOrDefault(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p), update.PackageId, StringComparison.OrdinalIgnoreCase))
            ?? installedDlls[0];

        var verdict = Verify(primary);

        if (_pluginOptions.TrustMode == PluginTrustMode.RequireTrustedPublisher)
        {
            if (IsTrustedPublisher(verdict)) return true;
            _logger.LogWarning(
                "Auto-update of {PackageId} to {Version} rolled back: the new binary is not signed by a trusted publisher (signed={IsSigned}, thumbprint={Thumbprint}).",
                update.PackageId, update.LatestVersion, verdict.IsSigned, verdict.SignerThumbprint);
            return false;
        }

        if (verdict.IsSigned) return true;

        _logger.LogWarning(
            "Auto-update of {PackageId} to {Version} rolled back: the new binary is unsigned, which requires the interactive trust prompt. Left for a manual install.",
            update.PackageId, update.LatestVersion);
        return false;
    }

    private bool IsTrustedPublisher(SignatureVerdict verdict)
        => verdict.IsSigned
            && verdict.SignerThumbprint is { } thumbprint
            && _pluginOptions.TrustedPublisherThumbprints.Contains(thumbprint, StringComparer.OrdinalIgnoreCase);

    private SignatureVerdict Verify(string dllPath)
    {
        try
        {
            return _signatureVerifier.Verify(dllPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signature verification threw for {Path}; treating as unsigned.", dllPath);
            return SignatureVerdict.NotSigned;
        }
    }

    private async Task<bool> ReloadAsync(string dllPath, CancellationToken cancellationToken)
    {
        try
        {
            return await _loader.LoadOrReloadAsync(dllPath, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hot-load of {Path} failed during auto-update.", dllPath);
            return false;
        }
    }
}
