namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Host-provided clipboard adapter. Plugins write to the system clipboard
/// through this interface rather than referencing WPF/WinForms types directly,
/// keeping the contracts assembly platform-neutral and the implementation
/// testable and mockable.
/// </summary>
/// <remarks>
/// <strong>Security:</strong> the clipboard is a shared system resource visible
/// to all running applications. Only place content here at the user's explicit
/// request (e.g. a "Copy" menu item), and prefer short-lived secrets over
/// long-lived ones. Never auto-copy sensitive values without user intent.
/// </remarks>
public interface IClipboard
{
    /// <summary>Writes <paramref name="text"/> to the system clipboard.</summary>
    void SetText(string text);

    /// <summary>
    /// Clears the system clipboard, but only if its current text still equals
    /// <paramref name="expectedText"/> — so anything the user copied afterward is
    /// left untouched. A no-op when the clipboard holds different content (or is
    /// empty). Use to auto-expire a short-lived secret (e.g. a copied password)
    /// after a timeout without clobbering the user's later clipboard activity.
    /// </summary>
    /// <remarks>
    /// Default-implemented as a no-op so plugins built against this member keep
    /// loading on hosts that predate it; the host overrides it with a real
    /// implementation.
    /// </remarks>
    void ClearIfMatches(string expectedText) { }
}
