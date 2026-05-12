using System;

namespace AzureTray.Plugin.Contracts;

// Optional secondary interface a plugin can implement alongside ITrayPlugin to
// tell the host its menu should be rebuilt — for example, when background data
// (pending approvals, eligible roles) finishes refreshing. The host rebuilds
// even when the menu is currently open so the user sees fresh state without
// having to close and reopen.
//
// Implementations may fire MenuChanged from any thread; the host marshals back
// to the UI thread before touching menu controls.
public interface IMenuChangeNotifier
{
    event Action MenuChanged;
}
