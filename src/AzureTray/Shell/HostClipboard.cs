using System;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Shell;

// Implementation of IClipboard backed by WPF's System.Windows.Clipboard.
// Lives in the host project (not the contracts) so plugins don't pull in
// PresentationCore just to copy a password.
//
// Clipboard.SetText occasionally throws a COMException when another process
// has the clipboard open (e.g. Office hooking the clipboard chain). We
// swallow that exception and log — losing a password to the clipboard is
// non-fatal; the user can retry from the menu.
public sealed class HostClipboard : IClipboard
{
    private readonly ILogger<HostClipboard> _logger;

    public HostClipboard(ILogger<HostClipboard> logger)
    {
        _logger = logger;
    }

    public void SetText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write to the system clipboard.");
        }
    }
}
