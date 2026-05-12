using System;
using System.Collections.Generic;
using System.Linq;
using Serilog.Events;

namespace AzureTray.Logging;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogEventLevel Level,
    string Category,
    string Message,
    Exception? Exception);

public sealed class LogRingBuffer
{
    private const int DefaultCapacity = 500;

    private readonly LinkedList<LogEntry> _entries = new();
    private readonly Lock _lock = new();
    private readonly int _capacity;

    public LogRingBuffer() : this(DefaultCapacity) { }

    public LogRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public event Action<LogEntry>? EntryAdded;

    // Count of currently-buffered entries at Error or Fatal level.
    public int ErrorCount { get; private set; }

    // Count of currently-buffered entries at Warning level.
    public int WarningCount { get; private set; }

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _entries.AddLast(entry);
            BumpCount(entry.Level, delta: +1);

            while (_entries.Count > _capacity)
            {
                var evicted = _entries.First!.Value;
                _entries.RemoveFirst();
                BumpCount(evicted.Level, delta: -1);
            }
        }

        EntryAdded?.Invoke(entry);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            ErrorCount = 0;
            WarningCount = 0;
        }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }

    private void BumpCount(LogEventLevel level, int delta)
    {
        if (level >= LogEventLevel.Error)
        {
            ErrorCount = Math.Max(0, ErrorCount + delta);
        }
        else if (level == LogEventLevel.Warning)
        {
            WarningCount = Math.Max(0, WarningCount + delta);
        }
    }
}
