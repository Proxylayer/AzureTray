namespace AzureTray.Logging;

// Runtime toggle for the disk-log sink. Wired into Serilog via WriteTo.Conditional
// so flipping Enabled at runtime starts / stops file writes without rebuilding
// the logger pipeline. Initial value comes from LoggingOptions.LogToDisk.
public sealed class FileLoggingSwitch
{
    public FileLoggingSwitch(bool initiallyEnabled)
    {
        Enabled = initiallyEnabled;
    }

    public bool Enabled { get; set; }
}
