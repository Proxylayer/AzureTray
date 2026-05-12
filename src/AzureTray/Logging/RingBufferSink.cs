using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace AzureTray.Logging;

public sealed class RingBufferSink : ILogEventSink
{
    private readonly LogRingBuffer _buffer;

    public RingBufferSink(LogRingBuffer buffer)
    {
        _buffer = buffer;
    }

    public void Emit(LogEvent logEvent)
    {
        // Use RenderMessage() so the DataGrid's Message column shows only the
        // formatted message — Timestamp / Level / Category live in their own
        // columns, so rendering the full output template here would duplicate
        // them in every row.
        var message = logEvent.RenderMessage(CultureInfo.InvariantCulture);

        var category = logEvent.Properties.TryGetValue("SourceContext", out var sc) && sc is ScalarValue { Value: string s }
            ? s
            : string.Empty;

        _buffer.Add(new LogEntry(
            logEvent.Timestamp,
            logEvent.Level,
            category,
            message,
            logEvent.Exception));
    }
}
