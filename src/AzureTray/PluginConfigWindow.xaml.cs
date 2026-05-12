using System;
using System.Linq;
using System.Windows;
using AzureTray.Plugins;
using AzureTray.ViewModels;

namespace AzureTray;

public partial class PluginConfigWindow : Window
{
    private IPluginLoader? _loader;
    private string? _pluginId;

    public PluginConfigWindow()
    {
        InitializeComponent();
    }

    // Configure the window for a specific plugin. The owning view model is
    // already in the SettingsViewModel's PluginConfigs collection — we just
    // surface it here as DataContext so the bindings light up. We also
    // subscribe to the loader's PluginsChanged event so the window auto-
    // closes if the plugin is unloaded (otherwise its bindings would point
    // at a torn-down view model and the user could still toggle dead state).
    public void Configure(PluginConfigViewModel viewModel, IPluginLoader loader)
    {
        DataContext = viewModel;
        Title = $"Configure {viewModel.DisplayName}";

        _pluginId = viewModel.PluginId;
        _loader = loader;
        _loader.PluginsChanged += OnPluginsChanged;
        Closed += (_, _) =>
        {
            if (_loader is not null) _loader.PluginsChanged -= OnPluginsChanged;
        };
    }

    private void OnPluginsChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || _loader is null || _pluginId is null) return;

        void Apply()
        {
            var stillLoaded = _loader.LoadedPlugins.Any(p =>
                string.Equals(p.Plugin.Id, _pluginId, StringComparison.OrdinalIgnoreCase));
            if (!stillLoaded && IsVisible) Close();
        }

        if (dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(new Action(Apply));
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();
}
