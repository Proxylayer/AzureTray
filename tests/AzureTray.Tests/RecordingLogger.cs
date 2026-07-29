using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzureTray.Tests;

// Captures formatted log messages so "refuses and says why" can be told apart
// from "silently does nothing". Same shape as the RecordingLogger the PIM
// watcher tests use, generic so it can stand in for ILogger<T>.
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
        => NullLogger.Instance.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));

    public bool HasMessageContaining(LogLevel level, string fragment)
        => Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public bool HasMessageContaining(string fragment)
        => Entries.Any(e => e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
