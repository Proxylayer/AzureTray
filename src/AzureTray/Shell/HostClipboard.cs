using System;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Shell;

// Implementation of IClipboard backed by WPF's System.Windows.Clipboard.
// Lives in the host project (not the contracts) so plugins don't pull in
// PresentationCore just to copy a password.
//
// Windows Clipboard.SetText requires STA-mode threading because the
// underlying OLE clipboard API isn't thread-safe. Plugins call this from
// whatever thread their async pipeline lands on — frequently a
// thread-pool thread — so we marshal to the WPF dispatcher's UI thread
// (which is guaranteed STA) before touching the clipboard.
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

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _logger.LogWarning("Clipboard write skipped — WPF Application is not running.");
            return;
        }

        // Already on the UI thread: write directly. Otherwise marshal and
        // wait so the caller can rely on the write having landed before
        // SetText returns. Failure (locked clipboard) is swallowed.
        if (dispatcher.CheckAccess())
        {
            TrySet(text);
        }
        else
        {
            dispatcher.Invoke(() => TrySet(text));
        }
    }

    private void TrySet(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write to the system clipboard.");
        }
    }

    public void ClearIfMatches(string expectedText)
    {
        if (string.IsNullOrEmpty(expectedText)) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _logger.LogWarning("Clipboard clear skipped — WPF Application is not running.");
            return;
        }

        if (dispatcher.CheckAccess())
        {
            TryClearIfMatches(expectedText);
        }
        else
        {
            dispatcher.Invoke(() => TryClearIfMatches(expectedText));
        }
    }

    // Compare-then-clear must happen on a single STA hop so nothing can swap
    // the clipboard out between the read and the clear. We only wipe when the
    // current contents are still the exact value we were asked to expire — a
    // value the user copied afterward is left alone.
    private void TryClearIfMatches(string expectedText)
    {
        try
        {
            if (System.Windows.Clipboard.ContainsText() &&
                string.Equals(System.Windows.Clipboard.GetText(), expectedText, StringComparison.Ordinal))
            {
                System.Windows.Clipboard.Clear();
                _logger.LogDebug("Cleared an expired secret from the clipboard.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear the system clipboard.");
        }
    }
}
