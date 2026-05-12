using System;
using System.Collections.Generic;

namespace AzureTray.Plugin.Contracts;

// Optional. Implementing this lets a plugin appear in the host's Settings
// window with its own option editors. Plugins that don't implement this
// still get the per-tenant enable grid the host provides for every plugin.
//
// Generic rendering vs. custom UI:
//   * Declare Options{} and the host renders a checkbox / textbox / numeric
//     stepper per entry based on Kind. The values stream back via SetValue
//     and read back via Values.
//   * Return a non-null BuildSettingsView() to take over the section
//     entirely. The host hosts the returned control without further
//     interpretation. The return type is typed as object so the contracts
//     assembly stays free of WPF — host casts to System.Windows.Controls.UserControl.
//
// A plugin may provide both: the generic options render above a custom
// UserControl, useful when most settings are simple but one needs richer UI.
public interface IPluginConfigurable
{
    IReadOnlyList<PluginOption> Options { get; }

    IReadOnlyDictionary<string, object?> Values { get; }

    // Called by the host when the user edits an option in Settings. Plugins
    // that need to react (refresh a menu, restart a watcher) should do so
    // here or via ValuesChanged.
    void SetValue(string key, object? value);

    event Action? ValuesChanged;

    // Return null to skip and let the host render only the generic option
    // list. Return a WPF UserControl (typed as object so contracts stays
    // platform-neutral) to render a custom editor under the generic list.
    object? BuildSettingsView();
}
