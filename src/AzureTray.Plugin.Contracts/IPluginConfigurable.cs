using System;
using System.Collections.Generic;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Optional interface that lets a plugin appear in the host's Settings window
/// with its own option editors. Plugins that don't implement this still get the
/// per-tenant enable grid the host provides for every plugin.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Generic rendering:</strong> declare <see cref="Options"/> and the
/// host renders a check-box, text box, numeric stepper, combo-box, or masked
/// password box per entry based on <see cref="PluginOptionKind"/>. Values
/// stream back via <see cref="SetValue"/> and are read via <see cref="Values"/>.
/// </para>
/// <para>
/// <strong>Custom UI:</strong> return a non-null <see cref="BuildSettingsView"/>
/// to take over the section entirely. The host hosts the returned
/// <c>UserControl</c> without further interpretation. The return type is
/// <c>object</c> to keep the contracts assembly free of WPF.
/// </para>
/// <para>
/// Both can coexist: generic options render above the custom control.
/// </para>
/// <para>
/// <strong>Security:</strong> use <see cref="PluginOptionKind.Secret"/> for any
/// credential or API key — the host stores it encrypted and masks it in the UI.
/// </para>
/// </remarks>
public interface IPluginConfigurable
{
    /// <summary>Options to render in the generic settings grid.</summary>
    IReadOnlyList<PluginOption> Options { get; }

    /// <summary>Current values keyed by <see cref="PluginOption.Key"/>.</summary>
    IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>
    /// Called by the host when the user edits an option. React here or via
    /// <see cref="ValuesChanged"/> (e.g. restart a background poller,
    /// invalidate a cache, fire <see cref="IMenuChangeNotifier.MenuChanged"/>).
    /// </summary>
    void SetValue(string key, object? value);

    /// <summary>Fired after <see cref="SetValue"/> changes one or more values.</summary>
    event Action? ValuesChanged;

    /// <summary>
    /// Return <c>null</c> to use the generic option grid only.
    /// Return a WPF <c>UserControl</c> (typed as <c>object</c> to keep
    /// contracts platform-neutral) to render a custom editor below the grid.
    /// </summary>
    object? BuildSettingsView();
}
