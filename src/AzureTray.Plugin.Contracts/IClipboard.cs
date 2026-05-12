namespace AzureTray.Plugin.Contracts;

// Host-provided clipboard adapter. Plugins reach the system clipboard through
// this interface rather than referencing WPF / WinForms types directly, so
// the contracts assembly stays platform-neutral and the host can swap the
// implementation (testable, mockable).
public interface IClipboard
{
    void SetText(string text);
}
