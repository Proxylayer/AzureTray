using Serilog.Events;

namespace AzureTray.Configuration;

public sealed class LoggingOptions
{
    public const string SectionName = "App:Logging";

    public LogEventLevel MinimumLevel { get; init; } = LogEventLevel.Information;

    public int RetainedFileCount { get; init; } = 14;

    // When true, log events are written to %LOCALAPPDATA%\AzureTray.Data\logs\
    // in addition to the in-memory ring buffer. Toggleable at runtime from the
    // Log Viewer window; this value is the startup default.
    public bool LogToDisk { get; init; } = true;

    // Max size per rolling file before a fresh one starts. Combined with the
    // daily rolling interval, prevents a chatty day from producing one
    // enormous file.
    public int FileSizeLimitMegabytes { get; init; } = 10;
}
