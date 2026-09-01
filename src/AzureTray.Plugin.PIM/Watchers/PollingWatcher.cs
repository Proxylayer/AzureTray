using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.PIM.Watchers;

// The poll loop every PIM watcher runs: start on a token linked to the plugin's
// lifetime, poll, survive a failed poll, wait the interval, repeat, and stop
// cleanly on request. All three watchers carried this verbatim; only the poll
// body, the wording of the failure line, and whether anything has to happen
// before the loop starts ever differed.
//
// Also home to the once-then-quiet fetch logging, because two of the three
// watchers now need it and the pattern is worth exactly one copy.
internal abstract class PollingWatcher
{
    private readonly TimeSpan _interval;

    private Task? _loopTask;
    private CancellationTokenSource? _cts;

    protected PollingWatcher(IPluginContext context, PluginTenant tenant, TimeSpan interval)
    {
        Context = context;
        Tenant = tenant;
        _interval = interval;
    }

    protected IPluginContext Context { get; }

    protected PluginTenant Tenant { get; }

    public string TenantId => Tenant.TenantId;

    public void Start(CancellationToken stopToken)
    {
        OnStarting();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
    }

    // Runs on the caller's thread before the loop is spun up, for a watcher that
    // has something to show before its first poll lands. Does nothing by default.
    protected virtual void OnStarting()
    {
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
    }

    // Names this watcher in the Warning raised when a whole poll throws
    // ("Eligible-role", "Pending-approval", ...). A value rather than a template
    // so the structured message template stays a constant.
    protected abstract string PollKind { get; }

    internal abstract Task PollAsync(CancellationToken cancellationToken);

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Context.Logger.LogWarning(
                    ex,
                    "{PollKind} poll failed for tenant {TenantId}; will retry next interval.",
                    PollKind, Tenant.TenantId);
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // One gate per fetch source. `subject` names the fetch ("Graph eligible-role
    // fetch") and `consequence` says what the caller degrades to ("continuing
    // with ARM only"), so the wording stays specific to the source while only
    // the once-then-quiet mechanics are shared. See FetchFailureGate.
    protected void LogFetchRecovered(FetchFailureGate gate, string subject)
    {
        if (gate.RecordSuccess())
        {
            Context.Logger.LogInformation(
                "{Subject} recovered for tenant {TenantId}.",
                subject, Tenant.TenantId);
        }
    }

    protected void LogFetchFailure(FetchFailureGate gate, Exception ex, string subject, string consequence)
    {
        // Azure.Identity's AuthenticationRequiredException means "the silent
        // token path can't recover; interactive sign-in needed". The host's
        // TenantAuthHealthService already logs that transition once at Warning
        // and raises the re-auth prompt, so this watcher stays at Debug even
        // for the FIRST occurrence. The plugin runs in its own load context
        // and deliberately doesn't reference Azure.Identity (the host's copy
        // would be a different assembly identity anyway), so the type is
        // matched by full name.
        var knownCondition = ex.GetType().FullName == "Azure.Identity.AuthenticationRequiredException";

        switch (gate.RecordFailure(knownCondition))
        {
            case FetchFailureGate.FailureLog.WarnWithException:
                Context.Logger.LogWarning(
                    ex,
                    "{Subject} failed for tenant {TenantId}; {Consequence}.",
                    subject, Tenant.TenantId, consequence);
                break;

            case FetchFailureGate.FailureLog.DebugOneLine:
                Context.Logger.LogDebug(
                    "{Subject} failing for tenant {TenantId} ({ExceptionType}: {ExceptionMessage}); {Consequence}.",
                    subject, Tenant.TenantId, ex.GetType().Name, ex.Message, consequence);
                break;
        }
    }
}
