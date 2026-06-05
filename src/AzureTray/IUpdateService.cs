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

    // Applies the update already downloaded by CheckOnStartupAsync and restarts,
    // WITHOUT another network round-trip. This is what the "update available"
    // banner / Install button should call: the bits are already on disk, so a
    // flaky or offline network (or a feed that no longer reports the staged
    // release as "new") must not block the install. Falls back to a full
    // check+download+apply when nothing has been pre-downloaded yet.
    Task<string> ApplyPendingUpdateAndRestartAsync();
}
