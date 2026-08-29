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

    // While the user is driving the menu by keyboard the cursor is usually
    // parked outside every menu window, which the hover poll would read as
    // "walked away" and dismiss after ~300 ms — making keyboard use
    // impossible. Any keyboard interaction (or a keyboard tray activation)
    // sets this; the poll then skips the outside-dismiss until the cursor
    // re-enters a menu, at which point mouse semantics resume unchanged.
    private static bool _keyboardNavActive;

    /// <summary>
    /// Marks the menu chain as keyboard-driven so the hover poll's
    /// outside-the-menu auto-dismiss is suspended until the mouse re-enters
    /// a menu. Called on keyboard interaction and by TrayIcon when the menu
    /// was opened via keyboard activation of the tray icon.
    /// </summary>
    internal static void NotifyKeyboardActivation() => _keyboardNavActive = true;

    private readonly TrayMenuWindow? _parent;
    // Mutable (not readonly) so an in-place refresh can swap in the fresh
    // menu item's provider — the delegate closes over the plugin's data
    // snapshot, so keeping the old one would keep serving stale results.
    private Func<string, IReadOnlyList<PluginMenuItem>>? _searchProvider;
    private TrayMenuWindow? _activeSubmenu;
    private PluginMenuItem? _activeSubmenuFor;
    // Whether _activeSubmenu is a right-click context popup (built from
    // ContextItems) rather than a hover submenu (built from Children /
    // SearchProvider). An in-place refresh must repopulate it from the
    // matching source on the fresh parent item.
    private bool _activeSubmenuIsContext;
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

    // ─── In-place refresh (MenuChanged while the menu is open) ───────────
    //
    // TrayIcon.RefreshOpenMenu rebuilds the ROOT item set when a plugin
    // fires MenuChanged; before this existed the rebuild stopped there, so
    // an open submenu kept its construction-time snapshot — stale rows and
    // a spinner that never started — until the user re-hovered. RefreshItems
    // swaps this window's rows and then cascades down the open child chain,
    // re-anchoring each child to its counterpart in the fresh collection
    // (MenuItemMatcher: Key first, Text with count-suffix tolerance as the
    // fallback) so hover dedup, spinner triggers, and search results all see
    // the fresh PluginMenuItem instances.

    /// <summary>
    /// Replaces this window's items with a freshly built set and refreshes
    /// any open child submenu / context popup in place. Children whose
    /// parent item no longer exists in the fresh set are closed.
    /// </summary>
    internal void RefreshItems(IReadOnlyList<PluginMenuItem> freshItems)
    {
        ReplaceItems(freshItems);
        RefreshActiveSubmenu();
    }

    // Swap the ObservableCollection contents, keeping the scroll position —
    // Clear() resets the ScrollViewer to the top, which would visibly jump
    // a long menu the user has scrolled. The search box (if any) is a
    // separate control and keeps its text untouched.
    private void ReplaceItems(IReadOnlyList<PluginMenuItem> freshItems)
    {
        var offset = _itemsScroll?.VerticalOffset ?? 0;

        Items.Clear();
        foreach (var item in freshItems) Items.Add(item);

        if (offset > 0) _itemsScroll?.ScrollToVerticalOffset(offset);
    }

    private void RefreshActiveSubmenu()
    {
        if (_activeSubmenu is null || _activeSubmenuFor is null) return;

        var freshParent = MenuItemMatcher.FindRefreshedParent(_activeSubmenuFor, Items);
        if (freshParent is null)
        {
            // The row the submenu hangs off no longer exists — close the
            // child and everything below it.
            _activeSubmenu.CloseChain();
            return;
        }

        // Re-anchor the hover dedup (OpenSubmenu / the poll's leaf-row check
        // compare by reference) to the instance that now lives in Items, so
        // hovering the row doesn't tear down the submenu we just refreshed.
        _activeSubmenuFor = freshParent;
        _activeSubmenu.RefreshFromParent(freshParent, _activeSubmenuIsContext);
    }

    // Repopulate this window from the refreshed parent item it was opened
    // from, then cascade to this window's own child.
    private void RefreshFromParent(PluginMenuItem freshParent, bool isContext)
    {
        if (isContext)
        {
            if (freshParent.ContextItems is not { Count: > 0 } ctx)
            {
                CloseChain();
                return;
            }
            RefreshItems(ctx);
            return;
        }

        if (HasSearch)
        {
            if (freshParent.SearchProvider is null)
            {
                // The row stopped being searchable; this window's search box
                // visibility was fixed at construction, so rebuild-by-close.
                CloseChain();
                return;
            }

            _searchProvider = freshParent.SearchProvider;
            IReadOnlyList<PluginMenuItem> results;
            // Preserve what the user has typed: re-run the fresh provider
            // with the current query instead of resetting to "".
            try { results = _searchProvider(SearchBox.Text ?? string.Empty); }
            catch { return; }
            RefreshItems(results);
            return;
        }

        if (freshParent.SearchProvider is not null)
        {
            // Plain submenu became searchable — needs the search box, which
            // only exists on windows constructed with a provider.
            CloseChain();
            return;
        }

        RefreshItems(freshParent.Children ?? Array.Empty<PluginMenuItem>());
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
            // The list must hold keyboard focus for Up/Down/Enter to work the
            // moment the menu opens. Searchable flyouts are excluded: their
            // SearchBox is auto-focused by OpenSubmenu and Down moves from it
            // into the results — focusing the list here would steal that.
            if (!HasSearch) ItemsList.Focus();
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
            // Next menu open starts under mouse semantics until a key is hit.
            _keyboardNavActive = false;
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
            // Keyboard-driven session: the cursor being elsewhere is expected,
            // not a dismissal signal. Dismissal then comes from Esc, an
            // invoke, or focus loss (OnDeactivated) instead.
            if (_keyboardNavActive)
            {
                _outsideTicks = 0;
                return;
            }

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
        // The cursor is back over a menu: hand control back to the mouse so
        // hover-open/close and outside-dismiss behave exactly as before.
        _keyboardNavActive = false;

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
                // Cursor is over a LEAF row in this menu. If the menu has an
                // open submenu (or right-click context popup) anchored to a
                // DIFFERENT row, the user has moved on — close it. The
                // same-row guard is essential for right-click context popups:
                // the originating row is a leaf and is still under the cursor
                // when the next poll tick fires, so without it the popup would
                // be torn down ~60 ms after appearing ("vanishes instantly").
                _hoveredSubmenuRow = null;
                _hoveredSubmenuParent = null;
                _onSubmenuRowTicks = 0;
                if (!ReferenceEquals(menu._activeSubmenuFor, item))
                {
                    menu._activeSubmenu?.CloseChain();
                }
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
        // Iterate most-recently-opened first so a child menu wins when it
        // visually overlaps its parent — right-click context popups open AT
        // the cursor (inside the parent's bounds), so without this reverse
        // order the parent would claim the overlap and the hit-test would
        // resolve to a different parent row, prompting the leaf-row branch
        // to close the popup the moment the user moved into it.
        for (var i = OpenMenus.Count - 1; i >= 0; i--)
        {
            var m = OpenMenus[i];
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

    // Favorite star: a dedicated focusable ToggleButton on the right of the
    // row. Clicking (or Space/Enter while it has focus) toggles favorite
    // state even on disabled (greyed/active) rows, never opens the submenu,
    // and never dismisses the menu — the glyph just flips in place.
    // ButtonBase handles the underlying mouse events itself, so the row's
    // MouseLeftButtonUp invoke path never sees a star click.
    private void OnFavoriteStarClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PluginMenuItem item }
            && item.OnToggleFavorite is not null)
        {
            ToggleFavorite(item);
        }
        e.Handled = true;
    }

    private void OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        var row = FindAncestor<ListBoxItem>(element);
        if (row?.DataContext is not PluginMenuItem item) return;
        if (item.IsSeparator) return;

        if (!item.IsEnabled) return;

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

    // Right-click opens the row's ContextItems as a popup anchored at the
    // cursor — independent of the left-click action and available even on
    // disabled (greyed) rows. Used for secondary actions like Copy / Revoke on
    // an active item without making the row a hover-expanding submenu.
    private void OnItemMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        var row = FindAncestor<ListBoxItem>(element);
        if (row?.DataContext is not PluginMenuItem item) return;
        if (item.IsSeparator || item.ContextItems is not { Count: > 0 } ctx) return;

        e.Handled = true;

        var cursor = System.Windows.Forms.Cursor.Position;
        OpenContextPopup(item, ctx, cursor.X, cursor.Y);
    }

    // Opens the row's ContextItems as a popup anchored at the given screen
    // point. Reuses the submenu plumbing so the close/hover logic and
    // InvokeAndDismiss-on-click all work unchanged. Shared by the mouse
    // right-click path (anchored at the cursor) and the keyboard
    // Shift+F10 / Apps path (anchored at the row).
    private void OpenContextPopup(
        PluginMenuItem item,
        IReadOnlyList<PluginMenuItem> contextItems,
        int screenX,
        int screenY)
    {
        // Close any open submenu/context chain first.
        _activeSubmenu?.CloseChain();

        var menu = new TrayMenuWindow(contextItems, parent: this);
        menu.ShowAt(screenX, screenY, openAboveAnchor: false);

        _activeSubmenu    = menu;
        _activeSubmenuFor = item;
        _activeSubmenuIsContext = true;
    }

    private void OpenSubmenu(PluginMenuItem item, ListBoxItem row, bool focusFirstItem = false)
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
        _activeSubmenuIsContext = false;

        // Auto-focus the search box so the user can type immediately.
        if (item.SearchProvider is not null)
        {
            submenu.Dispatcher.BeginInvoke(new Action(() =>
                submenu.SearchBox.Focus()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (focusFirstItem)
        {
            // Keyboard-opened (Right arrow / Enter on a parent row): move the
            // highlight into the child so arrows keep working there. Deferred
            // to Loaded so the item containers exist. Mouse-opened submenus
            // skip this — hover drives them and pre-selecting a row would add
            // a highlight the mouse user never asked for.
            submenu.Dispatcher.BeginInvoke(new Action(() =>
            {
                var first = MenuKeyboardNavigation.FindFirstSelectableIndex(submenu.Items);
                if (first >= 0) submenu.ItemsList.SelectedIndex = first;
                submenu.ItemsList.Focus();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
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

    // Esc closes this level and everything below it (chain semantics as
    // before), now also handing focus back to the parent menu's list so a
    // keyboard user lands where they left off.
    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            NotifyKeyboardActivation();
            CloseLevelAndReturnFocus();
            e.Handled = true;
        }
    }

    // ─── Keyboard navigation ─────────────────────────────────────────────
    //
    // PreviewKeyDown (not KeyDown) so these rules win over the ListBox's own
    // directional navigation, which neither skips separators/disabled rows
    // nor knows about submenus. Mouse behavior is untouched: nothing here
    // runs until a key is pressed, and NotifyKeyboardActivation only
    // suspends the hover poll's outside-dismiss until the cursor returns.
    //
    // Keys: Up/Down move the highlight (skip separators + disabled, wrap);
    // Enter/Space invoke a leaf or open a submenu (focusing its first item);
    // Right opens a parent row's submenu; Left closes a child level back to
    // its parent; Esc closes the level chain; Shift+F10 / Apps opens the
    // row's right-click context popup; Ctrl+F toggles the row's favorite.
    // In a searchable flyout the SearchBox keeps all typing keys — Down
    // moves into the results and Enter invokes the selected (else first)
    // result.
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var inSearchBox = HasSearch && SearchBox.IsKeyboardFocusWithin;
        // The favorite star is a real ToggleButton: while it has focus its
        // own Space/Enter handling toggles it — don't double-handle.
        var onFavoriteToggle = Keyboard.FocusedElement is System.Windows.Controls.Primitives.ToggleButton;

        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                if (inSearchBox && e.Key == Key.Up) return; // caret stays in the box
                NotifyKeyboardActivation();
                MoveSelection(e.Key == Key.Down ? +1 : -1);
                if (inSearchBox) ItemsList.Focus();          // Down enters the results
                e.Handled = true;
                return;

            case Key.Return:
                if (onFavoriteToggle) return;
                NotifyKeyboardActivation();
                if (ActivateRowAt(inSearchBox ? SelectedOrFirstIndex() : ItemsList.SelectedIndex))
                {
                    e.Handled = true;
                }
                return;

            case Key.Space:
                if (inSearchBox || onFavoriteToggle) return; // typing / star toggle
                NotifyKeyboardActivation();
                if (ActivateRowAt(ItemsList.SelectedIndex)) e.Handled = true;
                return;

            case Key.Right:
                if (inSearchBox) return;                     // caret movement
                NotifyKeyboardActivation();
                if (OpenSubmenuForSelectedRow()) e.Handled = true;
                return;

            case Key.Left:
                if (inSearchBox) return;                     // caret movement
                if (_parent is null) return;                 // root: nothing to fold into
                NotifyKeyboardActivation();
                CloseLevelAndReturnFocus();
                e.Handled = true;
                return;

            case Key.F10 when Keyboard.Modifiers == ModifierKeys.Shift:
            case Key.Apps:
                NotifyKeyboardActivation();
                if (OpenContextPopupForSelectedRow()) e.Handled = true;
                return;

            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                NotifyKeyboardActivation();
                if (ToggleFavoriteForSelectedRow()) e.Handled = true;
                return;
        }
    }

    // The selected row's index when it can carry the highlight, else the
    // first selectable row — the "Enter in the search box" target rule.
    private int SelectedOrFirstIndex()
        => MenuKeyboardNavigation.IsSelectable(Items, ItemsList.SelectedIndex)
            ? ItemsList.SelectedIndex
            : MenuKeyboardNavigation.FindFirstSelectableIndex(Items);

    private void MoveSelection(int direction)
    {
        var next = MenuKeyboardNavigation.FindNextSelectableIndex(Items, ItemsList.SelectedIndex, direction);
        if (next < 0) return;
        ItemsList.SelectedIndex = next;
        ItemsList.ScrollIntoView(ItemsList.SelectedItem);
    }

    // Same code path a click takes: invoke a leaf (InvokeAndDismiss) or open
    // a parent row's submenu — keyboard-opened submenus also get their first
    // item highlighted. Returns whether anything was actually activated.
    private bool ActivateRowAt(int index)
    {
        if (!MenuKeyboardNavigation.IsSelectable(Items, index)) return false;
        var item = Items[index];

        if (item.HasChildren)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row) return false;
            ItemsList.SelectedIndex = index;
            OpenSubmenu(item, row, focusFirstItem: true);
            return true;
        }

        if (item.Invoke is null) return false;
        InvokeAndDismiss(item);
        return true;
    }

    private bool OpenSubmenuForSelectedRow()
    {
        var index = ItemsList.SelectedIndex;
        if (!MenuKeyboardNavigation.IsSelectable(Items, index)) return false;
        if (!Items[index].HasChildren) return false;
        return ActivateRowAt(index);
    }

    // Close this level (and everything below it) and hand focus back to the
    // parent menu's list, which still holds the parent row's selection.
    private void CloseLevelAndReturnFocus()
    {
        var parent = _parent;
        CloseChain();
        if (parent is { IsVisible: true })
        {
            parent.Activate();
            parent.ItemsList.Focus();
        }
    }

    // Shift+F10 / Apps: the keyboard equivalent of right-clicking the
    // selected row — anchored at the row instead of the cursor.
    private bool OpenContextPopupForSelectedRow()
    {
        var index = ItemsList.SelectedIndex;
        if (index < 0 || index >= Items.Count) return false;
        var item = Items[index];
        if (item.IsSeparator || item.ContextItems is not { Count: > 0 } ctx) return false;
        if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem row) return false;

        var anchor = row.PointToScreen(new System.Windows.Point(row.ActualWidth / 2, row.ActualHeight));
        OpenContextPopup(item, ctx, (int)anchor.X, (int)anchor.Y);
        return true;
    }

    private bool ToggleFavoriteForSelectedRow()
    {
        var index = ItemsList.SelectedIndex;
        if (index < 0 || index >= Items.Count) return false;
        var item = Items[index];
        if (item.IsSeparator || item.OnToggleFavorite is null) return false;

        ToggleFavorite(item);
        // ToggleFavorite swaps the row's item instance; keep the highlight on
        // the same row so repeated Ctrl+F keeps working.
        ItemsList.SelectedIndex = index;
        return true;
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

    private void ToggleFavorite(PluginMenuItem item)
    {
        try { item.OnToggleFavorite?.Invoke(); }
        catch (Exception ex)
        {
            // Same containment as InvokeAndDismiss: a plugin's toggle handler
            // must never tear down the menu dispatcher.
            Serilog.Log.Logger.Error(ex, "Favorite toggle for {Text} threw.", item.Text);
        }

        // PluginMenuItem is immutable, so reflect the new state by swapping in
        // a copy with the flipped flag. This re-renders only this row's star —
        // no reorder, no full rebuild. The plugin's own favorites store was
        // updated by OnToggleFavorite, so the next menu open re-sorts. Match by
        // reference (not IndexOf) so a value-equal twin row isn't picked.
        for (var i = 0; i < Items.Count; i++)
        {
            if (ReferenceEquals(Items[i], item))
            {
                Items[i] = item with { IsFavorite = !(item.IsFavorite ?? false) };
                break;
            }
        }

        // A click can accumulate outside-ticks while the button is down; reset
        // so the hover poll doesn't auto-dismiss right after the toggle.
        ResetHoverState();
    }
}
