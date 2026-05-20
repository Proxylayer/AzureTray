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
}
