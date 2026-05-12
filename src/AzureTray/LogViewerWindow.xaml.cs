using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AzureTray.Logging;
using AzureTray.Shell;
using AzureTray.ViewModels;

namespace AzureTray;

public partial class LogViewerWindow : Window
{
    private readonly LogViewerViewModel _viewModel;

    public LogViewerWindow(LogViewerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        this.EnableDarkTitleBar();

        if (viewModel.Entries is INotifyCollectionChanged source)
        {
            source.CollectionChanged += OnEntriesChanged;
        }

        Closed += (_, _) =>
        {
            if (viewModel.Entries is INotifyCollectionChanged src)
            {
                src.CollectionChanged -= OnEntriesChanged;
            }
            (viewModel as IDisposable)?.Dispose();
        };
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_viewModel.AutoScroll) return;
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        // Wait for the ListBox to realise the new row before scrolling, so
        // ScrollIntoView lands on the right element. Background priority
        // fires after layout / item containers materialise.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var view = _viewModel.EntriesView;
            object? last = null;
            foreach (var item in view) last = item;
            if (last is not null)
            {
                EntriesList.ScrollIntoView(last);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

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
