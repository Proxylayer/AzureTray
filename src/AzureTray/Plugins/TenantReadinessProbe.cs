using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AzureTray.Auth;
using AzureTray.AzureCloud;
using AzureTray.Models;
using AzureTray.Plugin.Contracts;
using AzureTray.Tenants;

namespace AzureTray.Plugins;

// At startup, attempts a SILENT Graph-token round-trip for every configured
// tenant in parallel. Tenants whose cache + Windows-account fallback succeed
// silently are marked ready immediately. Tenants requiring interactive
// sign-in (cache miss / expired refresh token / different account) do NOT
// trigger an automatic broker popup — instead a stacked WPF notification
// appears with the tenant name and Login / Disable buttons, so the user
// sees what tenant is asking and why. Notifications never auto-dismiss.
//
// Tenants with ProbeDisabled = true are skipped entirely; the user opted
// out of being asked. The "Disable" button writes that flag; any successful
// SignInAsync clears it again.
public sealed class TenantReadinessProbe : IHostedService
{
    // Cap for the silent token attempt — pure cache + broker silent paths
    // should be fast; if a tenant takes longer we treat it as a fail and
    // offer the user the notification path.
    private static readonly TimeSpan SilentProbeTimeout = TimeSpan.FromSeconds(20);

    private const string LoginChoice = "Login";
    private const string DisableChoice = "Disable";

    private readonly ITenantStore _tenantStore;
    private readonly ICredentialFactory _credentials;
    private readonly IAzureCloudConfig _cloud;
    private readonly ITenantReadinessTracker _tracker;
    private readonly INotifier _notifier;
    private readonly ILogger<TenantReadinessProbe> _logger;

    public TenantReadinessProbe(
        ITenantStore tenantStore,
        ICredentialFactory credentials,
        IAzureCloudConfig cloud,
        ITenantReadinessTracker tracker,
        INotifier notifier,
        ILogger<TenantReadinessProbe> logger)
    {
        _tenantStore = tenantStore;
        _credentials = credentials;
        _cloud = cloud;
        _tracker = tracker;
        _notifier = notifier;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var tenants = _tenantStore.GetAll().ToArray();
        if (tenants.Length == 0)
        {
            _logger.LogInformation("No tenants configured; readiness probe has nothing to do.");
            return Task.CompletedTask;
        }

        // Fire-and-forget so the host doesn't block on potentially-slow token
        // round-trips. Each tenant probes independently.
        foreach (var tenant in tenants)
        {
            _ = ProbeAsync(tenant, cancellationToken);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ProbeAsync(Tenant tenant, CancellationToken hostToken)
    {
        if (tenant.ProbeDisabled)
        {
            _logger.LogInformation(
                "Skipping readiness probe for tenant {TenantId} ({DisplayName}) — probe disabled by user.",
                tenant.TenantId, tenant.DisplayName);
            return;
        }

        // Try silent first — the credential is configured with
        // DisableAutomaticAuthentication = true, so it serves cached tokens
        // and the broker's silent default-Windows-account path but throws
        // AuthenticationRequiredException instead of opening a window when
        // interaction would be needed.
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
            cts.CancelAfter(SilentProbeTimeout);

            var credential = _credentials.GetForTenant(tenant.TenantId);
            var ctx = new TokenRequestContext(new[] { _cloud.GraphScope });
            await credential.GetTokenAsync(ctx, cts.Token).ConfigureAwait(false);

            _tracker.MarkReady(new PluginTenant(tenant.TenantId, tenant.DisplayName));
            _logger.LogInformation(
                "Tenant {TenantId} ({DisplayName}) ready (silent).",
                tenant.TenantId, tenant.DisplayName);
            return;
        }
        catch (AuthenticationRequiredException ex)
        {
            _logger.LogInformation(
                ex,
                "Tenant {TenantId} ({DisplayName}) needs interactive sign-in; surfacing notification.",
                tenant.TenantId, tenant.DisplayName);
        }
        catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
        {
            return; // host shutdown
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Silent readiness probe for tenant {TenantId} ({DisplayName}) timed out after {Timeout}s; surfacing notification.",
                tenant.TenantId, tenant.DisplayName, SilentProbeTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Silent readiness probe for tenant {TenantId} ({DisplayName}) failed; surfacing notification.",
                tenant.TenantId, tenant.DisplayName);
        }

        await PromptForSignInAsync(tenant, hostToken).ConfigureAwait(false);
    }

    private async Task PromptForSignInAsync(Tenant tenant, CancellationToken hostToken)
    {
        // CancellationToken.None: notification must NOT auto-dismiss. Only
        // the user's button click resolves it. (The host shutdown path will
        // still tear down the dispatcher; that's acceptable.)
        var hint = string.IsNullOrWhiteSpace(tenant.SignInEmail)
            ? string.Empty
            : $" as {tenant.SignInEmail}";
        var request = new ChoiceRequest(
            Title: $"Sign in to {tenant.DisplayName}",
            Message: $"AzureTray needs to sign in to {tenant.DisplayName} ({tenant.TenantId}){hint}.\n\nClick Login to open the sign-in dialog, or Disable to silence this prompt for the tenant.",
            Choices: new[] { LoginChoice, DisableChoice },
            AllowOther: false);

        var result = await _notifier.ShowAsync(request, CancellationToken.None).ConfigureAwait(false);

        if (result is not ChoiceResult chosen || chosen.SelectedChoice is null)
        {
            _logger.LogInformation(
                "Sign-in notification dismissed without a choice for tenant {TenantId}.",
                tenant.TenantId);
            return;
        }

        if (string.Equals(chosen.SelectedChoice, LoginChoice, StringComparison.Ordinal))
        {
            await HandleLoginAsync(tenant, hostToken).ConfigureAwait(false);
        }
        else if (string.Equals(chosen.SelectedChoice, DisableChoice, StringComparison.Ordinal))
        {
            await HandleDisableAsync(tenant, hostToken).ConfigureAwait(false);
        }
    }

    private async Task HandleLoginAsync(Tenant tenant, CancellationToken hostToken)
    {
        try
        {
            await _credentials.SignInAsync(tenant.TenantId, hostToken).ConfigureAwait(false);

            // If the user previously hit Disable on this tenant, a successful
            // sign-in implies they want it back in the probe rotation.
            if (tenant.ProbeDisabled)
            {
                var reenabled = tenant with { ProbeDisabled = false };
                await _tenantStore.AddOrUpdateAsync(reenabled, hostToken).ConfigureAwait(false);
            }

            _tracker.MarkReady(new PluginTenant(tenant.TenantId, tenant.DisplayName));
            _logger.LogInformation(
                "Tenant {TenantId} ({DisplayName}) ready after user-initiated sign-in.",
                tenant.TenantId, tenant.DisplayName);
        }
        catch (OperationCanceledException) when (hostToken.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Interactive sign-in failed for tenant {TenantId} ({DisplayName}).",
                tenant.TenantId, tenant.DisplayName);

            // Surface the failure to the user so they know why nothing
            // happened after clicking Login. Without this, the prompt
            // simply disappears and the tenant silently stays in
            // not-ready state, which looks identical to "tenant got
            // removed" from the user's perspective.
            try
            {
                await _notifier.ShowAsync(
                    new InformationRequest(
                        $"Sign in to {tenant.DisplayName} failed",
                        $"{ex.Message}\n\nThe tenant is still in your list — open Settings or wait for the next launch to try again."),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception notifyEx)
            {
                _logger.LogWarning(notifyEx, "Failed to surface sign-in failure notification for tenant {TenantId}.", tenant.TenantId);
            }
        }
    }

    private async Task HandleDisableAsync(Tenant tenant, CancellationToken hostToken)
    {
        try
        {
            var disabled = tenant with { ProbeDisabled = true };
            await _tenantStore.AddOrUpdateAsync(disabled, hostToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Tenant {TenantId} ({DisplayName}) startup sign-in prompt disabled by user.",
                tenant.TenantId, tenant.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to persist Disable choice for tenant {TenantId}.",
                tenant.TenantId);
        }
    }
}
