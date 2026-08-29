using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AzureTray.Logging;
using AzureTray.Shell;
using AzureTray.ViewModels;

namespace AzureTray;

public partial class LogViewerWindow : Window
{
    private readonly LogViewerViewModel _viewModel;
    private ScrollViewer? _entriesScrollViewer;

    // Set whenever the entries view is refreshed (filter/search change) or
    // we scroll programmatically after one: a Reset can settle the viewport
    // near the bottom and would otherwise silently resume the tail. Cleared
    // on the next user-scroll ScrollChanged cycle.
    private bool _refreshPending;

    public LogViewerWindow(LogViewerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        this.EnableDarkTitleBar();

        viewModel.TailResumed += OnTailResumed;
        viewModel.EntriesViewRefreshed += OnEntriesViewRefreshed;

        Loaded += OnWindowLoaded;

        Closed += (_, _) =>
        {
            viewModel.TailResumed -= OnTailResumed;
            viewModel.EntriesViewRefreshed -= OnEntriesViewRefreshed;
            if (_entriesScrollViewer is not null)
            {
                _entriesScrollViewer.ScrollChanged -= OnEntriesScrollChanged;
            }
            (viewModel as IDisposable)?.Dispose();
        };
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _entriesScrollViewer = FindDescendant<ScrollViewer>(EntriesList);
        if (_entriesScrollViewer is not null)
        {
            _entriesScrollViewer.ScrollChanged += OnEntriesScrollChanged;
            if (_viewModel.AutoScroll && _viewModel.FollowTail)
            {
                _entriesScrollViewer.ScrollToEnd();
            }
        }
    }

    // Smart tail: distinguish "content grew" (extent changed — keep the pin
    // if following, never flips the follow state) from "the user scrolled"
    // (extent unchanged, offset moved — recompute the follow state from
    // proximity to the bottom).
    private void OnEntriesScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0)
        {
            if (_viewModel.AutoScroll && _viewModel.FollowTail)
            {
                _entriesScrollViewer?.ScrollToEnd();
            }
            return;
        }

        if (e.VerticalChange == 0) return;

        if (_refreshPending)
        {
            _refreshPending = false;
            return;
        }

        _viewModel.NotifyUserScroll(e.VerticalOffset, e.ViewportHeight, e.ExtentHeight);
    }

    private void OnTailResumed()
    {
        _entriesScrollViewer?.ScrollToEnd();
    }

    private void OnEntriesViewRefreshed()
    {
        _refreshPending = true;

        // While following, the extent-change branch re-pins to the end on
        // its own. While paused, restore the anchor: if the selected item
        // still passes the filter, bring it back into view — deferred so the
        // Reset's layout has settled and item containers exist.
        if (_viewModel.FollowTail) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            var selected = EntriesList.SelectedItem;
            if (selected is null || !_viewModel.EntriesView.Contains(selected)) return;
            _refreshPending = true; // ScrollIntoView must not flip the follow state
            EntriesList.ScrollIntoView(selected);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // Selecting a row is a "let me read this" gesture: pause the tail.
    // Only user selection counts — selection cleared by a removed item
    // (AddedItems empty) must not pause.
    private void OnEntriesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
        {
            _viewModel.PauseTail();
        }
    }

    private void OnEntriesContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _viewModel.PauseTail();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    // Search-box keyboard semantics: Enter hands focus to the entries list
    // (arrow keys then walk the results); Esc clears a non-empty search
    // immediately — marked handled so the window's IsCancel Close only fires
    // on the SECOND Esc, once the box is already empty.
    private void OnSearchBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EntriesList.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            _viewModel.ClearSearch();
            e.Handled = true;
        }
    }

    // Right-clicking anywhere inside a ListBoxItem should select that row
    // first so the context menu's Copy commands act on what the user
    // pointed at — not on whichever row was last clicked.
    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonDown(e);
        if (e.OriginalSource is DependencyObject element)
        {
            var item = FindAncestor<ListBoxItem>(element);
            if (item is not null)
            {
                item.IsSelected = true;
            }
        }
    }

    private void CopyRowClick(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not LogEntry entry) return;
        SafeSetClipboard(FormatRow(entry));
    }

    private void CopyMessageClick(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not LogEntry entry) return;
        if (!string.IsNullOrEmpty(entry.Message))
        {
            SafeSetClipboard(entry.Message);
        }
    }

    private void CopyExceptionClick(object sender, RoutedEventArgs e)
    {
        if (EntriesList.SelectedItem is not LogEntry entry) return;
        if (entry.Exception is { } ex)
        {
            SafeSetClipboard(ex.ToString());
        }
    }

    private void CopyAllRowsClick(object sender, RoutedEventArgs e)
    {
        var view = _viewModel.EntriesView;
        var sb = new StringBuilder();
        foreach (var item in view)
        {
            if (item is LogEntry entry)
            {
                sb.AppendLine(FormatRow(entry));
            }
        }
        if (sb.Length > 0)
        {
            SafeSetClipboard(sb.ToString());
        }
    }

    private static string FormatRow(LogEntry entry)
    {
        var ts = entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var category = string.IsNullOrWhiteSpace(entry.Category) ? string.Empty : $" {entry.Category}:";
        var message = entry.Message?.Replace(Environment.NewLine, " ") ?? string.Empty;
        var line = $"{ts} [{entry.Level}]{category} {message}";
        return entry.Exception is null ? line : $"{line}{Environment.NewLine}{entry.Exception}";
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        var current = start;
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // Clipboard.SetText throws on the rare COM race when something else is
    // holding it open. The Log Viewer is a non-critical surface — swallow it
    // so a single failure doesn't tear down the dispatcher.
    private static void SafeSetClipboard(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}
