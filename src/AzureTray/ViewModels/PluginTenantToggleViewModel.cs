using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureTray.ViewModels;

// One (plugin, tenant) row in the per-plugin enable grid. IsEnabled toggling
// streams back to the host's IPluginConfigStore via the commit callback.
public sealed partial class PluginTenantToggleViewModel : ObservableObject
{
    private readonly Action<bool> _commit;
    private bool _suspendCommit;

    public PluginTenantToggleViewModel(string tenantId, string displayName, bool isEnabled, Action<bool> commit)
    {
        TenantId = tenantId;
        DisplayName = displayName;
        _isEnabled = isEnabled;
        _commit = commit;
    }

    public string TenantId { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suspendCommit) return;
        _commit(value);
    }

    public void SetEnabledFromStore(bool value)
    {
        _suspendCommit = true;
        IsEnabled = value;
        _suspendCommit = false;
    }
}
