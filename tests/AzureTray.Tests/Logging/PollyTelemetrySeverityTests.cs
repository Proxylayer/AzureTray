using AzureTray.Logging;
using Polly;
using Polly.Telemetry;
using Xunit;

namespace AzureTray.Tests.Logging;

// Pins the Tier-3 severity mapping: only Error events are ever touched, and of
// those only per-attempt-timeout events and the final-handled ExecutionAttempt
// duplicate are demoted to Warning (below the Serilog "Polly" -> Error
// override's visibility bar). Everything else keeps its severity.
public sealed class PollyTelemetrySeverityTests
{
    [Theory]
    [InlineData(ResilienceEventSeverity.Warning)]
    [InlineData(ResilienceEventSeverity.Information)]
    public void Map_NonErrorSeverity_PassesThroughUnchanged(ResilienceEventSeverity severity)
    {
        // Even from sources/events that WOULD be demoted at Error severity.
        var args = Args(
            severity,
            eventName: PollyTelemetrySeverity.ExecutionAttemptEventName,
            strategyName: PollyTelemetrySeverity.AttemptTimeoutStrategyName);

        Assert.Equal(severity, PollyTelemetrySeverity.Map(args));
    }

    [Fact]
    public void Map_ErrorFromAttemptTimeoutStrategy_DemotedToWarning()
    {
        var args = Args(
            ResilienceEventSeverity.Error,
            eventName: "OnTimeout",
            strategyName: PollyTelemetrySeverity.AttemptTimeoutStrategyName);

        Assert.Equal(ResilienceEventSeverity.Warning, PollyTelemetrySeverity.Map(args));
    }

    [Fact]
    public void Map_ErrorExecutionAttempt_DemotedToWarning()
    {
        // Polly reports ExecutionAttempt at Error only for the final handled
        // attempt — the Tier-1 duplicate this mapping exists to silence.
        var args = Args(
            ResilienceEventSeverity.Error,
            eventName: PollyTelemetrySeverity.ExecutionAttemptEventName,
            strategyName: "Standard-Retry");

        Assert.Equal(ResilienceEventSeverity.Warning, PollyTelemetrySeverity.Map(args));
    }

    [Fact]
    public void Map_ErrorCircuitOpened_StaysError()
    {
        var args = Args(
            ResilienceEventSeverity.Error,
            eventName: "OnCircuitOpened",
            strategyName: "Standard-CircuitBreaker");

        Assert.Equal(ResilienceEventSeverity.Error, PollyTelemetrySeverity.Map(args));
    }

    [Fact]
    public void Map_ErrorTotalRequestTimeout_StaysError()
    {
        // Same event name as the attempt timeout ("OnTimeout") but a different
        // strategy — only Standard-AttemptTimeout is demoted.
        var args = Args(
            ResilienceEventSeverity.Error,
            eventName: "OnTimeout",
            strategyName: "Standard-TotalRequestTimeout");

        Assert.Equal(ResilienceEventSeverity.Error, PollyTelemetrySeverity.Map(args));
    }

    private static SeverityProviderArguments Args(
        ResilienceEventSeverity severity, string eventName, string strategyName)
    {
        var source = new ResilienceTelemetrySource(
            pipelineName: "test-pipeline",
            pipelineInstanceName: "test-instance",
            strategyName: strategyName);
        var resilienceEvent = new ResilienceEvent(severity, eventName);
        var context = ResilienceContextPool.Shared.Get();
        try
        {
            return new SeverityProviderArguments(source, resilienceEvent, context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}
