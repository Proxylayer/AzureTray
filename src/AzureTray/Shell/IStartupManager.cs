namespace AzureTray.Shell;

// Manages the "launch at Windows sign-in" registration for the current user.
// Backed by HKCU\Software\Microsoft\Windows\CurrentVersion\Run — per-user,
// no elevation needed, and the path under %LOCALAPPDATA%\AzureTray\current\
// stays stable across Velopack upgrades.
public interface IStartupManager
{
    bool IsEnabled();
    void Enable();
    void Disable();
}
