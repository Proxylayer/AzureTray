using System;
using Microsoft.Win32;

namespace AzureTray.Shell;

public sealed class RegistryStartupManager : IStartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AzureTray";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var existing = key?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(existing)) return false;

        // Treat a stale registration (pointing somewhere else) as not enabled
        // for this install — flipping the checkbox will then rewrite it.
        return string.Equals(NormalizeCommand(existing), NormalizeCommand(BuildCommand()), StringComparison.OrdinalIgnoreCase);
    }

    public void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Unable to open HKCU\\{RunKeyPath}.");
        key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;
        if (key.GetValue(ValueName) is null) return;
        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    // Quote the path so spaces in profile names (e.g. "C:\Users\Jane Doe\…")
    // don't get split into argv at launch.
    private static string BuildCommand()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is unavailable.");
        return $"\"{exe}\"";
    }

    private static string NormalizeCommand(string value)
        => value.Trim().Trim('"');
}
