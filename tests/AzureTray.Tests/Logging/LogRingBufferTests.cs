using System;
using AzureTray.Logging;
using Serilog.Events;
using Xunit;

namespace AzureTray.Tests.Logging;

public sealed class LogRingBufferTests
{
    [Fact]
    public void Add_AppendsAndSnapshotReturnsAllEntries()
    {
        var buffer = new LogRingBuffer(capacity: 5);
        var a = Entry("first");
        var b = Entry("second");

        buffer.Add(a);
        buffer.Add(b);

        var snapshot = buffer.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("first", snapshot[0].Message);
        Assert.Equal("second", snapshot[1].Message);
    }

    [Fact]
    public void Add_DropsOldestEntryWhenCapacityExceeded()
    {
        var buffer = new LogRingBuffer(capacity: 2);

        buffer.Add(Entry("a"));
        buffer.Add(Entry("b"));
        buffer.Add(Entry("c"));

        var snapshot = buffer.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("b", snapshot[0].Message);
        Assert.Equal("c", snapshot[1].Message);
    }

    [Fact]
    public void Add_RaisesEntryAddedEvent()
    {
        var buffer = new LogRingBuffer(capacity: 5);
        LogEntry? observed = null;
        buffer.EntryAdded += e => observed = e;

        var entry = Entry("hello");
        buffer.Add(entry);

        Assert.NotNull(observed);
        Assert.Equal("hello", observed!.Message);
    }

    [Fact]
    public void Constructor_ThrowsOnNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogRingBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogRingBuffer(-1));
    }

    [Fact]
    public void Add_TracksErrorAndWarningCounts()
    {
        var buffer = new LogRingBuffer(capacity: 10);

        Assert.Equal(0, buffer.ErrorCount);
        Assert.Equal(0, buffer.WarningCount);

        buffer.Add(Entry("info", LogEventLevel.Information));
        buffer.Add(Entry("warn-1", LogEventLevel.Warning));
        buffer.Add(Entry("warn-2", LogEventLevel.Warning));
        buffer.Add(Entry("error", LogEventLevel.Error));
        buffer.Add(Entry("fatal", LogEventLevel.Fatal));

        Assert.Equal(2, buffer.ErrorCount); // error + fatal both count as error-level
        Assert.Equal(2, buffer.WarningCount);
    }

    [Fact]
    public void Add_EvictingCountedEntry_DecrementsTheCount()
    {
        var buffer = new LogRingBuffer(capacity: 2);

        buffer.Add(Entry("error-1", LogEventLevel.Error));
        buffer.Add(Entry("warn-1", LogEventLevel.Warning));
        Assert.Equal(1, buffer.ErrorCount);
        Assert.Equal(1, buffer.WarningCount);

        buffer.Add(Entry("info", LogEventLevel.Information)); // evicts error-1
        Assert.Equal(0, buffer.ErrorCount);
        Assert.Equal(1, buffer.WarningCount);

        buffer.Add(Entry("info-2", LogEventLevel.Information)); // evicts warn-1
        Assert.Equal(0, buffer.ErrorCount);
        Assert.Equal(0, buffer.WarningCount);
    }

    [Fact]
    public void Clear_EmptiesEntriesAndResetsCounts()
    {
        var buffer = new LogRingBuffer(capacity: 5);
        buffer.Add(Entry("error", LogEventLevel.Error));
        buffer.Add(Entry("warn", LogEventLevel.Warning));

        buffer.Clear();

        Assert.Empty(buffer.Snapshot());
        Assert.Equal(0, buffer.ErrorCount);
        Assert.Equal(0, buffer.WarningCount);
    }

    private static LogEntry Entry(string message, LogEventLevel level = LogEventLevel.Information)
        => new(DateTimeOffset.UtcNow, level, "Test", message, null);
}
