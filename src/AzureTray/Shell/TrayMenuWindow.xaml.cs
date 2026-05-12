using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Shell;

// Borderless, transparent WPF window used as the tray context menu. Replaces
// the WinForms ContextMenuStrip TrayIcon used to drive so the menu picks up
// every Theme.xaml token, the spinner animates cleanly, and submenus follow
// the same look as the root menu.
//
// Submenus open as additional TrayMenuWindow instances anchored to the right
// of the parent row. The Activated/Deactivated chain across instances is what
// keeps the parent open while a submenu has focus and closes everything when
// the user clicks outside the whole chain.
public partial class TrayMenuWindow : Window
{
    // Every visible menu in the chain (root + any open submenus). Static so
    // hover polling can look across windows without traversing parents.
    private static readonly List<TrayMenuWindow> OpenMenus = new();

    // Single global polling timer drives both behaviors: dismiss when the
    // cursor has been outside every open menu for ~300ms, and auto-open a
    // submenu when the cursor has lingered on a parent row for ~300ms.
    // Polling is more reliable than MouseEnter/Leave on transparent multi-
    // window menus where bubbling and hit-testing get inconsistent.
    private const int PollIntervalMs = 60;
    private const int CloseAfterTicks = 5;   // ~300 ms outside menus → dismiss
    private const int OpenAfterTicks = 5;    // ~300 ms hovering parent row → open submenu

    private static DispatcherTimer? _hoverTimer;
    private static int _outsideTicks;
    private static int _onSubmenuRowTicks;
    private static System.Windows.Controls.ListBoxItem? _hoveredSubmenuRow;
    private static TrayMenuWindow? _hoveredSubmenuParent;

    private readonly TrayMenuWindow? _parent;
    private readonly Func<string, IReadOnlyList<PluginMenuItem>>? _searchProvider;
    private TrayMenuWindow? _activeSubmenu;
    private PluginMenuItem? _activeSubmenuFor;
    private bool _isClosing;

    public ObservableCollection<PluginMenuItem> Items { get; }
    public bool HasSearch => _searchProvider is not null;
    public string SearchPlaceholder { get; }

    public TrayMenuWindow(
        IEnumerable<PluginMenuItem> items,
        TrayMenuWindow? parent = null,
        Func<string, IReadOnlyList<PluginMenuItem>>? searchProvider = null,
        string? searchPlaceholder = null)
    {
        InitializeComponent();
        Items = new ObservableCollection<PluginMenuItem>(items);
        _parent = parent;
        _searchProvider = searchProvider;
        SearchPlaceholder = searchPlaceholder ?? "Search…";
        DataContext = this;
    }

    // ─── Scroll arrows (legacy Azure.PIM.Tray pattern) ───────────────────
    //
    // The ListBox hides its scrollbar; instead two small ▲ / ▼ borders
    // appear at the top/bottom of the menu when there's more to scroll.
    // Hovering an arrow starts a 50ms DispatcherTimer that scrolls 40 DIP
    // per tick (a smooth auto-scroll); MouseLeave stops the timer.

    // ~1 row per tick at 300ms keeps auto-scroll readable — at 50ms it
    // jumped to the end before the user could react.
    private const double ScrollStepDip = 40;
    private static readonly TimeSpan ScrollTickInterval = TimeSpan.FromMilliseconds(300);

    private ScrollViewer? _itemsScroll;
    private DispatcherTimer? _scrollTimer;

    private void OnItemsListLoaded(object sender, RoutedEventArgs e)
    {
        if (_itemsScroll is not null) return;
        _itemsScroll = FindVisualChild<ScrollViewer>(ItemsList);
        if (_itemsScroll is null) return;

        _itemsScroll.ScrollChanged += OnItemsScrollChanged;
        UpdateScrollArrowVisibility();
    }

    private void OnItemsScrollChanged(object sender, ScrollChangedEventArgs e)
        => UpdateScrollArrowVisibility();

    private void UpdateScrollArrowVisibility()
    {
        if (_itemsScroll is null) return;

        // Up arrow shows once any vertical scroll has happened; down arrow
        // shows while there's still more to scroll. -1 fudge avoids the
        // sub-pixel "still 0.4 DIP to go" case where the user is already
        // visually at the bottom.
        ScrollUpArrow.Visibility = _itemsScroll.VerticalOffset > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScrollDownArrow.Visibility = _itemsScroll.VerticalOffset < _itemsScroll.ScrollableHeight - 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnScrollUpArrowMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => StartAutoScroll(-ScrollStepDip);

    private void OnScrollDownArrowMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        => StartAutoScroll(ScrollStepDip);

    private void OnScrollArrowMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        => StopAutoScroll();

    private void StartAutoScroll(double deltaDip)
    {
        if (_itemsScroll is null) return;

        StopAutoScroll();

        // Step once immediately so a quick hover registers without
        // waiting a full tick.
        _itemsScroll.ScrollToVerticalOffset(_itemsScroll.VerticalOffset + deltaDip);

        _scrollTimer = new DispatcherTimer { Interval = ScrollTickInterval };
        _scrollTimer.Tick += (_, _) =>
        {
            if (_itemsScroll is null) return;
            _itemsScroll.ScrollToVerticalOffset(_itemsScroll.VerticalOffset + deltaDip);
        };
        _scrollTimer.Start();
    }

    private void StopAutoScroll()
    {
        _scrollTimer?.Stop();
        _scrollTimer = null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var deeper = FindVisualChild<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    // Plugin's SearchProvider is called on every keystroke. The list
    // rebuilds in place via the ObservableCollection so the user doesn't
    // see a flicker. No debounce yet — provider is expected to be cheap
    // (it's a local filter in every current use). Add a DispatcherTimer-
    // based debounce here if/when a provider becomes async.
    private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_searchProvider is null) return;
        if (sender is not System.Windows.Controls.TextBox box) return;

        IReadOnlyList<PluginMenuItem> results;
        try { results = _searchProvider(box.Text ?? string.Empty); }
        catch { return; }

        Items.Clear();
        foreach (var item in results) Items.Add(item);
    }

    // Opens at a specific screen point in PIXELS. The window's drop shadow
    // pushes the actual border in by Margin; we account for that so the
    // anchor is the visual edge, not the layout edge.
    public void ShowAt(int screenX, int screenY, bool openAboveAnchor = false)
    {
        Show();
        // Layout must complete before we know the size to position above.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var dipX = screenX / dpi.DpiScaleX;
            var dipY = screenY / dpi.DpiScaleY;

            // CursorOverlap pushes the menu a few DIP toward the cursor so it
            // visibly overlaps the click point. Without this, the cursor lands
            // exactly on the 1 DIP border edge — sub-pixel rounding then judges
            // the cursor "outside" the hit area, and the 300 ms hover poll
            // dismisses the menu before the user can move into it.
            //
            // 3 DIP in both X and Y for the main (upward-opening) menu so the
            // cursor sits firmly inside the menu's bottom-right corner — the
            // 12 DIP terms below compensate for the shadow's outer margin.
            const double CursorOverlap = 3;
            if (openAboveAnchor)
            {
                Left = dipX - ActualWidth + 12 + CursorOverlap;       // right edge sits CursorOverlap DIP past the cursor (cursor is CursorOverlap inside from the right)
                Top = dipY - ActualHeight + 12 + CursorOverlap;       // bottom edge sits CursorOverlap DIP past the cursor
            }
            else
            {
                Left = dipX - 12;
                Top = dipY - 12 - CursorOverlap;                      // top edge sits CursorOverlap DIP above the cursor
            }
            // openAboveAnchor is the tray-icon click — the anchor (cursor) is
            // inside the taskbar, BELOW WorkingArea.Bottom. Clamping to the
            // work area would push the menu's bottom 12+ DIP above the cursor
            // and the cursor would be considered outside the hit area.
            // Allow the menu to extend down into the taskbar (still bounded
            // by screen bounds) so the cursor lands inside the menu.
            ClampToWorkArea(allowExtendIntoTaskbar: openAboveAnchor);
            Activate();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ClampToWorkArea(bool allowExtendIntoTaskbar = false)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var dpi = VisualTreeHelper.GetDpi(this);
        var workLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
        var workTop = screen.WorkingArea.Top / dpi.DpiScaleY;
        var workRight = screen.WorkingArea.Right / dpi.DpiScaleX;
        var workBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;
        var screenBottom = screen.Bounds.Bottom / dpi.DpiScaleY;

        var bottomLimit = allowExtendIntoTaskbar ? screenBottom : workBottom;

        if (Left + ActualWidth > workRight) Left = workRight - ActualWidth;
        if (Top + ActualHeight > bottomLimit) Top = bottomLimit - ActualHeight;
        if (Left < workLeft) Left = workLeft;
        if (Top < workTop) Top = workTop;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OpenMenus.Add(this);
        // Force the menu off-screen until ShowAt positions it, so we never
        // flash at the default 0,0 location while measuring.
        Left = -2000;
        Top = -2000;
        EnsurePollingTimerRunning();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Defensive: anyone who Close()'s this window (WPF lifecycle, alt-F4,
        // etc.) implicitly closes its open child too. CloseChain on a child
        // is idempotent if it already ran via the normal CloseChain path.
        if (_activeSubmenu is not null)
        {
            var child = _activeSubmenu;
            _activeSubmenu = null;
            _activeSubmenuFor = null;
            child.CloseChain();
        }

        OpenMenus.Remove(this);
        if (OpenMenus.Count == 0)
        {
            _hoverTimer?.Stop();
            ResetHoverState();
        }
        base.OnClosed(e);
    }

    private static void EnsurePollingTimerRunning()
    {
        if (_hoverTimer is null)
        {
            _hoverTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PollIntervalMs),
            };
            _hoverTimer.Tick += OnPollTick;
        }
        _hoverTimer.Start();
    }

    private static void ResetHoverState()
    {
        _outsideTicks = 0;
        _onSubmenuRowTicks = 0;
        _hoveredSubmenuRow = null;
        _hoveredSubmenuParent = null;
    }

    private static void OnPollTick(object? sender, EventArgs e)
    {
        if (OpenMenus.Count == 0)
        {
            _hoverTimer?.Stop();
            return;
        }

        var (menu, row) = HitTestCursor();

        if (menu is null)
        {
            // Cursor is outside every open menu. Wait CloseAfterTicks frames
            // (~300 ms total) before dismissing so accidental jiggles don't
            // close a menu mid-decision.
            _outsideTicks++;
            _onSubmenuRowTicks = 0;
            _hoveredSubmenuRow = null;
            _hoveredSubmenuParent = null;
            if (_outsideTicks >= CloseAfterTicks)
            {
                ResetHoverState();
                CloseRootMenu();
            }
            return;
        }

        _outsideTicks = 0;

        if (row?.DataContext is PluginMenuItem item && !item.IsSeparator)
        {
            if (item.HasChildren)
            {
                // Hovered a submenu parent — count ticks; open when stable.
                // OpenSubmenu dedups, so re-firing on subsequent ticks is harmless.
                if (ReferenceEquals(_hoveredSubmenuRow, row))
                {
                    _onSubmenuRowTicks++;
                    if (_onSubmenuRowTicks >= OpenAfterTicks)
                    {
                        menu.OpenSubmenu(item, row);
                    }
                }
                else
                {
                    _hoveredSubmenuRow = row;
                    _hoveredSubmenuParent = menu;
                    _onSubmenuRowTicks = 1;
                }
            }
            else
            {
                // Cursor is over a LEAF row in this menu. If this menu still
                // has an open submenu from a different row, the user has
                // moved on — close it. Without this, hovering "Log Viewer"
                // leaves "Pending Approvals" expanded next to it.
                _hoveredSubmenuRow = null;
                _hoveredSubmenuParent = null;
                _onSubmenuRowTicks = 0;
                menu._activeSubmenu?.CloseChain();
            }
        }
        else
        {
            // Separator, or hit fell in padding between rows — don't change
            // any submenu state. The user isn't pointing at anything they
            // could interact with, so leave the current view alone.
            _hoveredSubmenuRow = null;
            _hoveredSubmenuParent = null;
            _onSubmenuRowTicks = 0;
        }
    }

    private static void CloseRootMenu()
    {
        if (OpenMenus.Count == 0) return;
        var root = OpenMenus[0];
        while (root._parent is not null) root = root._parent;
        root.CloseChain();
    }

    private static (TrayMenuWindow? menu, System.Windows.Controls.ListBoxItem? row) HitTestCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        foreach (var m in OpenMenus)
        {
            if (!m.IsVisible) continue;
            var dpi = VisualTreeHelper.GetDpi(m);
            // The drop shadow consumes 12 DIPs of transparent margin
            // around the visible Border. Exclude it from the hit area so
            // the "is over menu" test matches what the user actually sees.
            const double shadow = 12;
            var leftPx = (m.Left + shadow) * dpi.DpiScaleX;
            var topPx = (m.Top + shadow) * dpi.DpiScaleY;
            var rightPx = (m.Left + m.ActualWidth - shadow) * dpi.DpiScaleX;
            var bottomPx = (m.Top + m.ActualHeight - shadow) * dpi.DpiScaleY;
            if (cursor.X < leftPx || cursor.X > rightPx
                || cursor.Y < topPx || cursor.Y > bottomPx)
            {
                continue;
            }

            // Inside this menu. Find which row (if any) is under the cursor
            // via a local hit-test, so we can decide about submenu open.
            var localDip = new System.Windows.Point(
                (cursor.X / dpi.DpiScaleX) - m.Left,
                (cursor.Y / dpi.DpiScaleY) - m.Top);
            var hit = VisualTreeHelper.HitTest(m, localDip);
            var row = hit?.VisualHit is null
                ? null
                : FindAncestor<System.Windows.Controls.ListBoxItem>(hit.VisualHit);
            return (m, row);
        }
        return (null, null);
    }

    // ListBox raises SelectionChanged when the keyboard moves the highlight.
    // We don't want a row to look "selected" persistently after the user has
    // moved on — clear so hover and selection share the same accent fill.
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb) lb.UnselectAll();
    }

    private void OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        var row = FindAncestor<ListBoxItem>(element);
        if (row?.DataContext is not PluginMenuItem item) return;
        if (item.IsSeparator || !item.IsEnabled) return;

        if (item.HasChildren)
        {
            // Toggle: clicking the same folder a second time closes its
            // submenu rather than no-op'ing through OpenSubmenu's dedup.
            // Without this, after a hover-opened submenu the only way for a
            // mouse-only user to dismiss the chain was to wait for the 300 ms
            // outside-hover timeout — which felt like the menu was "locked".
            if (ReferenceEquals(_activeSubmenuFor, item) && _activeSubmenu is { IsVisible: true })
            {
                _activeSubmenu.CloseChain();
                _activeSubmenu = null;
                _activeSubmenuFor = null;
                return;
            }

            OpenSubmenu(item, row);
        }
        else if (item.Invoke is not null)
        {
            InvokeAndDismiss(item);
        }
    }

    private void OpenSubmenu(PluginMenuItem item, ListBoxItem row)
    {
        // Dedup: re-entering the same parent row while its submenu is open
        // shouldn't rebuild and re-show; it'd close and reopen on every tick.
        if (ReferenceEquals(_activeSubmenuFor, item) && _activeSubmenu is { IsVisible: true })
        {
            return;
        }

        // CloseChain (not Close) so an existing submenu's own grandchild is
        // dismissed too — otherwise switching between sibling parent rows in
        // the same menu leaves the previous grandchild hanging in space.
        _activeSubmenu?.CloseChain();

        // Searchable submenus: initial items come from SearchProvider("")
        // and the host renders a search box at the top of the flyout.
        var initialItems = item.SearchProvider is not null
            ? item.SearchProvider(string.Empty)
            : item.Children ?? Array.Empty<PluginMenuItem>();

        var submenu = new TrayMenuWindow(
            initialItems,
            parent: this,
            searchProvider: item.SearchProvider,
            searchPlaceholder: item.SearchPlaceholder);

        // Anchor at the right edge of the parent row, vertically aligned to
        // its top. Convert WPF point → screen pixels for ShowAt's contract.
        var topRight = row.PointToScreen(new System.Windows.Point(row.ActualWidth, 0));
        submenu.ShowAt((int)topRight.X, (int)topRight.Y, openAboveAnchor: false);
        _activeSubmenu = submenu;
        _activeSubmenuFor = item;

        // Auto-focus the search box so the user can type immediately.
        if (item.SearchProvider is not null)
        {
            submenu.Dispatcher.BeginInvoke(new Action(() =>
                submenu.SearchBox.Focus()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void InvokeAndDismiss(PluginMenuItem item)
    {
        try { item.Invoke?.Invoke(); }
        catch (Exception ex)
        {
            // Plugin owns its own error handling; never let one tear down
            // the dispatcher because the user clicked their menu item.
            // But log it via the global Serilog logger so silent failures
            // ("I clicked X and nothing happened") show up in the Log Viewer
            // and on disk instead of vanishing.
            Serilog.Log.Logger.Error(
                ex,
                "Menu item {Text} threw during Invoke.",
                item.Text);
        }

        // KeepMenuOpen items (e.g. "↻ Refresh") fire their action but leave
        // the menu visible so the user can see the result update. Reset the
        // hover counters so the next tick decides afresh — without this, an
        // outside-ticks count could've accumulated during the click and the
        // menu would auto-dismiss seconds later.
        if (item.KeepMenuOpen)
        {
            ResetHoverState();
            return;
        }

        CloseChain();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Deferred so a newly-opening submenu (which fires its own Activated)
        // can mark itself active before we decide to dismiss. If any window
        // in the chain is still active we keep going; otherwise the user
        // clicked outside and we close everything.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing) return;
            if (OpenMenus.Any(m => m.IsActive)) return;
            CloseRoot();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseChain();
            e.Handled = true;
        }
    }


    // Close from this menu down through any open child submenus.
    private void CloseChain()
    {
        _isClosing = true;
        _activeSubmenu?.CloseChain();
        _activeSubmenu = null;
        _activeSubmenuFor = null;
        if (_parent is not null)
        {
            _parent._activeSubmenu = null;
            _parent._activeSubmenuFor = null;
        }
        Close();
    }

    // Close the entire chain starting from the root, so a click on a leaf
    // submenu item tears down the parent menu too.
    private void CloseRoot()
    {
        var root = this;
        while (root._parent is not null) root = root._parent;
        root.CloseChain();
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
