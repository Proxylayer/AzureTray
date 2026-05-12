using System;
using System.Threading.Tasks;

namespace AzureTray;

public interface IUpdateService
{
    string CurrentVersionDisplay { get; }

    // Set after a successful startup check detects a newer release.
    // Null when no update is pending. Cleared on next launch (each
    // process re-checks fresh).
    string? PendingUpdateVersion { get; }

    // Fired when CheckOnStartupAsync detects an update is available.
    // Argument is the available version string. Subscribers may run on
    // any thread; marshal to the UI dispatcher before touching WPF.
    event Action<string>? UpdateAvailable;

    Task CheckOnStartupAsync();
    Task<string> CheckAndApplyAsync();
}
