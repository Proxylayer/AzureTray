using Polly.Telemetry;

namespace AzureTray.Logging;

// Severity mapping for Polly's resilience telemetry — one half of the "quiet
// tiers" HTTP failure logging (the other half is the Serilog
// MinimumLevel.Override("Polly", Error) in Program.ConfigureLogging).
//
// The log tells a failed request's story in three tiers:
//   Tier 1 (transport, final outcome): HostPluginHttpClient logs ONE Warning
//     line per finally-failed request ("← ..." non-success / "× ..." thrown).
//     It sends through the named client, so the resilience handler's retries
//     resolve before that line is written — it is already the final outcome.
//   Tier 2 (call site, consequence): the operation's own catch block logs an
//     Error describing what actually failed ("Activation failed ...").
//   Tier 3 (Polly): ONLY genuinely serious pipeline events — circuit breaker
//     opened and total-request-timeout — deserve their own voice. Everything
//     per-attempt is retry mechanics that Tier 1 already summarizes.
//
// This provider decides what severity Polly REPORTS an event at; the Serilog
// override decides what is VISIBLE (Error and up). Two demotions to Warning
// (i.e. below the override's bar) implement Tier 3:
//
//   1. Any Error event from the standard handler's per-attempt timeout
//      strategy ("Standard-AttemptTimeout"). An attempt timeout is retried;
//      if the request ultimately fails, Tiers 1+2 record it. The
//      total-request-timeout strategy ("Standard-TotalRequestTimeout") is a
//      different Source.StrategyName and stays at Error.
//
//   2. The "ExecutionAttempt" event at Error severity. Polly reports
//      ExecutionAttempt at Error ONLY for the final attempt whose outcome was
//      handled (TelemetryUtil.ReportFinalExecutionAttempt); non-final handled
//      attempts are Warning and unhandled outcomes are Information. That
//      final-handled line ("Execution attempt ... Handled: 'True'") merely
//      duplicates Tier 1's Warning, so demoting Error ExecutionAttempt events
//      targets exactly the duplicate and nothing else. (The handled flag
//      itself is not exposed on SeverityProviderArguments, but the severity
//      already encodes it.)
//
// Circuit-breaker events ("OnCircuitOpened") and total-request-timeout
// "OnTimeout" events keep their Error severity and pass the Serilog override.
internal static class PollyTelemetrySeverity
{
    internal const string AttemptTimeoutStrategyName = "Standard-AttemptTimeout";
    internal const string ExecutionAttemptEventName = "ExecutionAttempt";

    internal static ResilienceEventSeverity Map(SeverityProviderArguments args)
    {
        if (args.Event.Severity != ResilienceEventSeverity.Error)
        {
            return args.Event.Severity;
        }

        if (args.Source.StrategyName == AttemptTimeoutStrategyName)
        {
            return ResilienceEventSeverity.Warning;
        }

        if (args.Event.EventName == ExecutionAttemptEventName)
        {
            return ResilienceEventSeverity.Warning;
        }

        return args.Event.Severity;
    }
}
