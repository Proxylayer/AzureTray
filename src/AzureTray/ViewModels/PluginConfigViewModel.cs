using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AzureTray.Plugin.Contracts;

namespace AzureTray.ViewModels;

// One loaded plugin's full editor: per-tenant enable grid, generic option
// list, and an optional custom UserControl returned from
// IPluginConfigurable.BuildSettingsView(). The XAML uses HasCustomView /
// HasOptions / HasTenants triggers to suppress empty sections.
public sealed partial class PluginConfigViewModel : ObservableObject
{
    public PluginConfigViewModel(
        string pluginId,
        string displayName,
        string version,
        bool isConfigurable,
        object? customView)
    {
        PluginId = pluginId;
        DisplayName = displayName;
        Version = version;
        IsConfigurable = isConfigurable;
        CustomView = customView;
    }

    public string PluginId { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public bool IsConfigurable { get; }
    public object? CustomView { get; }

    public bool HasCustomView => CustomView is not null;
    public bool HasOptions => Options.Count > 0;
    public bool HasTenants => Tenants.Count > 0;

    public ObservableCollection<PluginTenantToggleViewModel> Tenants { get; } = new();
    public ObservableCollection<PluginOptionViewModel> Options { get; } = new();
}
