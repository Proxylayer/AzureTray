using System;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Optional interface a plugin implements alongside <see cref="ITrayPlugin"/> to
/// signal that the host should rebuild its tray menu. Implement this whenever
/// the plugin has background data (polls, WebSocket events, timers) that may
/// arrive after <c>GetMenuItems</c> was last called.
/// </summary>
/// <remarks>
/// <see cref="MenuChanged"/> may be fired from any thread — the host marshals
/// back to the UI thread before touching menu controls.
/// </remarks>
public interface IMenuChangeNotifier
{
    /// <summary>
    /// Fire this event when the menu content has changed and the host should
    /// call <see cref="ITrayPlugin.GetMenuItems"/> again on the next repaint.
    /// The host rebuilds even if the menu is currently open, so the user sees
    /// fresh data without closing and reopening.
    /// </summary>
    event Action MenuChanged;
}
