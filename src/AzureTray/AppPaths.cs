using System;
using System.IO;

namespace AzureTray;

public interface IAppPaths
{
    string ConfigDir { get; }
    string DataDir { get; }
    string LogsDir { get; }
    string PluginsDir { get; }
    string PluginDataRoot { get; }
    string LogFileTemplate { get; }
    string UserConfigFilePath { get; }
    string TenantStoreFilePath { get; }
    string PluginConfigFilePath { get; }
    void EnsureDirectoriesExist();
}

public sealed class AppPaths : IAppPaths
{
    // Keep deliberately separate from %LOCALAPPDATA%\AzureTray\ because that path is owned
    // by Velopack — its app-x.y.z\, current\, packages\, Update.exe live there and the structure
    // is rewritten on every release. Putting logs/plugins under a sibling .Data folder guarantees
    // they survive updates.
    private const string ConfigFolderName = "AzureTray";
    private const string DataFolderName = "AzureTray.Data";

    // Dev/test isolation hook. When set, ALL app state (plugins, plugin-data,
    // config, tenants, logs, auth records) is rooted under this folder instead
    // of the real %LOCALAPPDATA% / %APPDATA% install, so a throwaway instance
    // never touches a production install. Unset in production: behaviour is
    // exactly as before. SingleInstanceLock reads the same variable to scope
    // its mutex, so an isolated instance can run alongside the real tray.
    internal const string DataRootOverrideEnvVar = "AZURETRAY_DATA_ROOT";

    public AppPaths()
        : this(ResolveLocalRoot(), ResolveRoamingRoot())
    {
    }

    private static string ResolveLocalRoot()
        => OverrideRoot() ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string ResolveRoamingRoot()
        => OverrideRoot() ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string? OverrideRoot()
    {
        var root = Environment.GetEnvironmentVariable(DataRootOverrideEnvVar);
        return string.IsNullOrWhiteSpace(root) ? null : root;
    }

    public AppPaths(string localAppDataRoot, string roamingAppDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingAppDataRoot);

        ConfigDir = Path.Combine(roamingAppDataRoot, ConfigFolderName);
        DataDir = Path.Combine(localAppDataRoot, DataFolderName);
        LogsDir = Path.Combine(DataDir, "logs");
        PluginsDir = Path.Combine(DataDir, "plugins");
        PluginDataRoot = Path.Combine(DataDir, "plugin-data");
        LogFileTemplate = Path.Combine(LogsDir, "app-.log");
        UserConfigFilePath = Path.Combine(ConfigDir, "config.json");
        TenantStoreFilePath = Path.Combine(ConfigDir, "tenants.json");
        PluginConfigFilePath = Path.Combine(ConfigDir, "plugin-config.json");
    }

    public string ConfigDir { get; }
    public string DataDir { get; }
    public string LogsDir { get; }
    public string PluginsDir { get; }
    public string PluginDataRoot { get; }
    public string LogFileTemplate { get; }
    public string UserConfigFilePath { get; }
    public string TenantStoreFilePath { get; }
    public string PluginConfigFilePath { get; }

    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(PluginsDir);
        Directory.CreateDirectory(PluginDataRoot);
    }
}
