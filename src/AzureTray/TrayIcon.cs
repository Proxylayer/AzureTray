using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AzureTray.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using AzureTray.Shell;
using WinForms = System.Windows.Forms;

namespace AzureTray;

// Owns the WinForms NotifyIcon (no WPF equivalent for the tray icon itself)
// and shows our fully-themed WPF TrayMenuWindow as the context menu when the
// user clicks it. The WinForms ContextMenuStrip is gone — every visible
// surface in the app now goes through Theme.xaml.
public sealed class TrayIcon : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TrayIcon> _logger;
    private readonly List<(IMenuChangeNotifier Notifier, Action Handler)> _menuChangeSubscriptions = new();
    private readonly List<(IBadgeProvider Provider, Action Handler)> _badgeSubscriptions = new();
    private Icon? _currentIcon;
    private WinForms.NotifyIcon? _notifyIcon;
    private TrayMenuWindow? _openMenu;
    private SettingsWindow? _settingsWindow;
    private LogViewerWindow? _logViewerWindow;
    // Cached IPluginLoader resolved at Start() — Dispose can't go back to the
    // service provider because host shutdown disposes the IServiceProvider
    // BEFORE Dispose() is invoked on the singletons it owns.
    private IPluginLoader? _pluginLoader;

    public TrayIcon(IServiceProvider services, ILogger<TrayIcon> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void Start()
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = CreateIcon(BadgeState.Normal, count: 0),
            Text = "PL Azure Tray",
            Visible = true,
        };

        _notifyIcon.MouseClick += OnTrayClick;
        SubscribeToPluginMenuChanges();
        SubscribeToBadgeProviders();

        // Re-wire MenuChanged subscriptions whenever the set of loaded plugins
        // changes so hot-installed plugins drive their busy animations through
        // the same path as plugins loaded at startup. Cache the loader so
        // Dispose() can unsubscribe without going back to the (by-then-
        // disposed) service provider.
        _pluginLoader = _services.GetService<IPluginLoader>();
        if (_pluginLoader is not null)
        {
            _pluginLoader.PluginsChanged += OnPluginsChanged;
        }
    }

    private void OnPluginsChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        void Apply()
        {
            UnsubscribeFromPluginMenuChanges();
            UnsubscribeFromBadgeProviders();
            SubscribeToPluginMenuChanges();
            SubscribeToBadgeProviders();
            RefreshIcon();
            OnPluginMenuChanged();   // refresh any currently-open menu
        }

        if (dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(new Action(Apply));
    }

    private void UnsubscribeFromPluginMenuChanges()
    {
        foreach (var (notifier, handler) in _menuChangeSubscriptions)
        {
            notifier.MenuChanged -= handler;
        }
        _menuChangeSubscriptions.Clear();
    }

    private void SubscribeToBadgeProviders()
    {
        var pluginLoader = _services.GetService<IPluginLoader>();
        if (pluginLoader is null) return;

        foreach (var loaded in pluginLoader.LoadedPlugins)
        {
            if (loaded.Plugin is not IBadgeProvider provider) continue;

            Action handler = OnBadgeChanged;
            provider.BadgeChanged += handler;
            _badgeSubscriptions.Add((provider, handler));
        }
    }

    private void UnsubscribeFromBadgeProviders()
    {
        foreach (var (provider, handler) in _badgeSubscriptions)
        {
            provider.BadgeChanged -= handler;
        }
        _badgeSubscriptions.Clear();
    }

    private void OnBadgeChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) RefreshIcon();
        else dispatcher.BeginInvoke(new Action(RefreshIcon));
    }

    // Walk every loaded badge provider, pick the most severe state and sum
    // counts. Most-severe order: Error > Pending > Update > Normal.
    private void RefreshIcon()
    {
        if (_notifyIcon is null) return;

        var pluginLoader = _services.GetService<IPluginLoader>();
        var state = BadgeState.Normal;
        var count = 0;
        if (pluginLoader is not null)
        {
            foreach (var loaded in pluginLoader.LoadedPlugins)
            {
                if (loaded.Plugin is not IBadgeProvider p) continue;
                count += Math.Max(0, p.Count);
                if (Severity(p.State) > Severity(state)) state = p.State;
            }
        }

        var newIcon = CreateIcon(state, count);
        _notifyIcon.Icon = newIcon;

        var previous = _currentIcon;
        _currentIcon = newIcon;
        previous?.Dispose();

        _notifyIcon.Text = count > 0
            ? $"PL Azure Tray — {count} pending"
            : "PL Azure Tray";
    }

    private static int Severity(BadgeState s) => s switch
    {
        BadgeState.Error => 3,
        BadgeState.Pending => 2,
        BadgeState.Update => 1,
        _ => 0,
    };

    private void OnTrayClick(object? sender, WinForms.MouseEventArgs e)
    {
        // Both left and right click open the menu. The menu is at the cursor;
        // tray icons live at the bottom-right corner of the primary monitor
        // on the standard Win11 layout so the menu opens above-and-left.
        if (e.Button != WinForms.MouseButtons.Left && e.Button != WinForms.MouseButtons.Right)
        {
            return;
        }
        // Hold Left Ctrl + click → admin menu with host-level actions
        // (reload plugins, open data/log folders, etc.). The regular menu
        // is plugin-driven; the admin menu is for managing the host itself.
        //
        // The user asked for Fn as the modifier, but Fn is handled by
        // keyboard firmware on most laptops and never reaches the OS, so
        // GetAsyncKeyState cannot detect it. Left Ctrl is the fallback.
        ShowMenu(admin: IsLeftCtrlDown());
    }

    private void ShowMenu(bool admin)
    {
        // Close any existing menu chain — clicking the tray again should
        // restart, not stack windows.
        if (_openMenu is not null)
        {
            try { _openMenu.Close(); } catch { /* already closed */ }
            _openMenu = null;
        }

        var items = admin ? BuildAdminMenuItems() : BuildMenuItems();
        var menu = new TrayMenuWindow(items);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMenu, menu)) _openMenu = null;
        };
        _openMenu = menu;

        var cursor = WinForms.Cursor.Position;
        menu.ShowAt(cursor.X, cursor.Y, openAboveAnchor: true);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_LCONTROL = 0xA2;

    private static bool IsLeftCtrlDown() =>
        (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;

    private List<PluginMenuItem> BuildMenuItems()
    {
        var items = new List<PluginMenuItem>();

        var pluginLoader = _services.GetService<IPluginLoader>();
        var addedAnyPluginItems = false;
        if (pluginLoader is not null)
        {
            foreach (var loaded in pluginLoader.LoadedPlugins)
            {
                IReadOnlyList<PluginMenuItem> pluginItems;
                try
                {
                    pluginItems = loaded.Plugin.GetMenuItems();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Plugin {Id} threw from GetMenuItems(); skipping its menu.", loaded.Plugin.Id);
                    continue;
                }

                foreach (var item in pluginItems)
                {
                    items.Add(item);
                    addedAnyPluginItems = true;
                }
            }
        }

        if (addedAnyPluginItems)
        {
            items.Add(PluginMenuItem.Separator);
        }

        items.Add(new PluginMenuItem(
            Text: "⚙  Settings…",
            Invoke: () => ShowSettings(admin: false)));

        items.Add(PluginMenuItem.Separator);

        items.Add(new PluginMenuItem(
            Text: BuildLogViewerLabel(),
            Invoke: ShowLogViewer));

        items.Add(PluginMenuItem.Separator);

        items.Add(new PluginMenuItem(
            Text: "Quit",
            Invoke: Quit));

        return items;
    }

    // Admin menu: shown when the user holds the Windows key + clicks the
    // tray icon. Host-level actions only — plugin menus are intentionally
    // omitted so the user can drive admin work without plugin clutter.
    private List<PluginMenuItem> BuildAdminMenuItems()
    {
        var items = new List<PluginMenuItem>
        {
            new PluginMenuItem(
                Text: "🛠  Admin menu",
                IsEnabled: false),
            PluginMenuItem.Separator,
            new PluginMenuItem(
                Text: "🔄  Reload all plugins",
                Invoke: () => _ = ReloadPluginsAsync()),
            new PluginMenuItem(
                Text: "📂  Open plugins folder",
                Invoke: () => OpenFolder(_services.GetService<IAppPaths>()?.PluginsDir)),
            new PluginMenuItem(
                Text: "📂  Open plugin data folder",
                Invoke: () => OpenFolder(_services.GetService<IAppPaths>()?.PluginDataRoot)),
            new PluginMenuItem(
                Text: "📂  Open logs folder",
                Invoke: () => OpenFolder(_services.GetService<IAppPaths>()?.LogsDir)),
            new PluginMenuItem(
                Text: "📂  Open config folder",
                Invoke: () => OpenFolder(_services.GetService<IAppPaths>()?.ConfigDir)),
            PluginMenuItem.Separator,
            new PluginMenuItem(
                Text: "🩺  Plugin status",
                Children: BuildPluginStatusItems()),
            new PluginMenuItem(
                Text: "🧪  Test runner…",
                Invoke: ShowTestRunner),
            PluginMenuItem.Separator,
            new PluginMenuItem(
                Text: "⚙  Settings (admin)…",
                Invoke: () => ShowSettings(admin: true)),
            new PluginMenuItem(
                Text: BuildLogViewerLabel(),
                Invoke: ShowLogViewer),
            PluginMenuItem.Separator,
            new PluginMenuItem(
                Text: "Quit",
                Invoke: Quit),
        };
        return items;
    }

    private IReadOnlyList<PluginMenuItem> BuildPluginStatusItems()
    {
        var loader = _services.GetService<IPluginLoader>();
        if (loader is null || loader.LoadedPlugins.Count == 0)
        {
            return new[] { new PluginMenuItem("(no plugins loaded)", IsEnabled: false) };
        }

        var rows = new List<PluginMenuItem>();
        foreach (var loaded in loader.LoadedPlugins)
        {
            rows.Add(new PluginMenuItem(
                Text: $"{loaded.Plugin.DisplayName}  v{loaded.Plugin.Version}",
                IsEnabled: false));
            rows.Add(new PluginMenuItem(
                Text: $"    {loaded.Plugin.Id}",
                IsEnabled: false));
        }
        return rows;
    }

    private async System.Threading.Tasks.Task ReloadPluginsAsync()
    {
        var loader = _services.GetService<IPluginLoader>();
        if (loader is null) return;
        try
        {
            await loader.UnloadAllAsync(System.Threading.CancellationToken.None);
            await loader.LoadAllAsync(System.Threading.CancellationToken.None);
            _logger.LogInformation("Admin: reloaded all plugins.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin: plugin reload failed.");
        }
    }

    private void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            System.IO.Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Admin: failed to open folder {Path}.", path);
        }
    }

    private string BuildLogViewerLabel()
    {
        var buffer = _services.GetService<LogRingBuffer>();
        if (buffer is null) return "📝  Log Viewer";

        if (buffer.ErrorCount > 0 && buffer.WarningCount > 0)
        {
            return $"📝  Log Viewer  (✕ {buffer.ErrorCount} / ⚠ {buffer.WarningCount})";
        }
        if (buffer.ErrorCount > 0)
        {
            return $"📝  Log Viewer  (✕ {buffer.ErrorCount} error{(buffer.ErrorCount == 1 ? "" : "s")})";
        }
        if (buffer.WarningCount > 0)
        {
            return $"📝  Log Viewer  (⚠ {buffer.WarningCount} warning{(buffer.WarningCount == 1 ? "" : "s")})";
        }
        return "📝  Log Viewer";
    }

    private void SubscribeToPluginMenuChanges()
    {
        var pluginLoader = _services.GetService<IPluginLoader>();
        if (pluginLoader is null) return;

        foreach (var loaded in pluginLoader.LoadedPlugins)
        {
            if (loaded.Plugin is not IMenuChangeNotifier notifier) continue;

            Action handler = OnPluginMenuChanged;
            notifier.MenuChanged += handler;
            _menuChangeSubscriptions.Add((notifier, handler));
        }
    }

    private void OnPluginMenuChanged()
    {
        // If a menu is currently open, swap its Items in place so a poll
        // that finishes while the user is mid-look refreshes the view
        // without closing the menu. Marshal to the UI thread.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (dispatcher.CheckAccess()) RefreshOpenMenu();
        else dispatcher.BeginInvoke(new Action(RefreshOpenMenu));
    }

    private void RefreshOpenMenu()
    {
        if (_openMenu is null) return;
        var fresh = BuildMenuItems();
        _openMenu.Items.Clear();
        foreach (var item in fresh) _openMenu.Items.Add(item);
    }

    private bool _settingsWindowIsAdmin;

    // Settings window is reused across opens. If the user switches from
    // the user-mode entry to the admin entry (or vice versa), close the
    // existing window and open a fresh one so Fix Permissions / Create
    // App Registration controls only appear in admin sessions.
    private void ShowSettings(bool admin)
    {
        if (_settingsWindow is not null && _settingsWindowIsAdmin != admin)
        {
            _settingsWindow.Close();
            _settingsWindow = null;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = _services.GetRequiredService<SettingsWindow>();
            _settingsWindowIsAdmin = admin;
            if (_settingsWindow.DataContext is ViewModels.SettingsViewModel vm)
            {
                vm.IsAdminMode = admin;
            }
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
        }
    }

    private TestRunnerWindow? _testRunnerWindow;

    private void ShowTestRunner()
    {
        if (_testRunnerWindow is null)
        {
            _testRunnerWindow = _services.GetRequiredService<TestRunnerWindow>();
            _testRunnerWindow.Closed += (_, _) => _testRunnerWindow = null;
            _testRunnerWindow.Show();
        }
        else
        {
            if (_testRunnerWindow.WindowState == WindowState.Minimized)
                _testRunnerWindow.WindowState = WindowState.Normal;
            _testRunnerWindow.Activate();
        }
    }

    private void ShowLogViewer()
    {
        if (_logViewerWindow is null)
        {
            _logViewerWindow = _services.GetRequiredService<LogViewerWindow>();
            _logViewerWindow.Closed += (_, _) => _logViewerWindow = null;
            _logViewerWindow.Show();
        }
        else
        {
            if (_logViewerWindow.WindowState == WindowState.Minimized)
                _logViewerWindow.WindowState = WindowState.Normal;
            _logViewerWindow.Activate();
        }
    }

    private static void Quit() => System.Windows.Application.Current.Shutdown();

    // Render the tray icon on the fly. 16×16 surface (32-bit ARGB) with a
    // rounded-square background coloured by state + "Az" glyph centred over
    // it. The count is reflected in the tooltip, not drawn on the icon —
    // 16 px is too small for legible numeric badges at standard DPI.
    private static Icon CreateIcon(BadgeState state, int count)
    {
        const int size = 16;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var bg = state switch
            {
                BadgeState.Pending => Color.FromArgb(0xCC, 0x44, 0x00),  // orange
                BadgeState.Update => Color.FromArgb(0x00, 0x88, 0x44),   // green
                BadgeState.Error => Color.FromArgb(0xC0, 0x39, 0x2B),    // red
                _ => Color.FromArgb(0x00, 0x66, 0xCC),                   // blue
            };
            using var brush = new SolidBrush(bg);
            using var path = RoundedRect(new Rectangle(0, 0, size, size), radius: 4);
            g.FillPath(brush, path);

            // "Az" centred. Segoe UI Semibold at 7.5pt fits 16 px and renders
            // legibly at 100 % DPI; bumps cleanly on high-DPI tray hosts.
            using var font = new Font("Segoe UI", 7.5f, System.Drawing.FontStyle.Bold, GraphicsUnit.Point);
            const string glyph = "Az";
            var glyphSize = g.MeasureString(glyph, font);
            var x = (size - glyphSize.Width) / 2f + 0.5f;
            var y = (size - glyphSize.Height) / 2f - 0.5f;
            g.DrawString(glyph, font, Brushes.White, x, y);
        }

        // Bitmap → HICON → Icon. The HICON must be destroyed once we wrap it
        // in a managed Icon — Icon.FromHandle takes ownership only when
        // constructed via the (handle, takeOwnership) overload, which is not
        // part of the public API. Use a clone so we can DestroyIcon freely.
        var hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        UnsubscribeFromPluginMenuChanges();
        UnsubscribeFromBadgeProviders();

        if (_pluginLoader is not null)
        {
            _pluginLoader.PluginsChanged -= OnPluginsChanged;
            _pluginLoader = null;
        }

        if (_openMenu is not null)
        {
            try { _openMenu.Close(); } catch { /* fine */ }
            _openMenu = null;
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
