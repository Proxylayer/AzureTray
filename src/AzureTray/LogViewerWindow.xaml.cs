using System;
using System.Collections.Specialized;
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

        // Scroll to whatever the filtered+sorted view currently considers last.
        // Hop to the dispatcher's Background priority so the grid finishes
        // realizing the new row before we try to scroll to it.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var view = _viewModel.EntriesView;
            object? last = null;
            foreach (var item in view) last = item;
            if (last is not null)
            {
                EntriesGrid.ScrollIntoView(last);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    // WPF's DataGrid doesn't change selection on right-click by default, so the
    // ContextMenu would operate on whichever cell was last selected. Walk up
    // from the click target to the System.Windows.Controls.DataGridCell, select + focus it, so Copy
    // cell / Copy row always act on what the user actually clicked.
    private void EntriesGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element is not null && element is not System.Windows.Controls.DataGridCell)
        {
            element = VisualTreeHelper.GetParent(element);
        }
        if (element is System.Windows.Controls.DataGridCell cell)
        {
            EntriesGrid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(cell.DataContext, cell.Column);
            cell.Focus();
        }
    }

    private void CopyCellClick(object sender, RoutedEventArgs e)
    {
        var cell = EntriesGrid.CurrentCell;
        if (cell.Column is null || cell.Item is not LogEntry entry) return;
        var text = ExtractCellText(entry, cell.Column);
        if (!string.IsNullOrEmpty(text))
        {
            SafeSetClipboard(text);
        }
    }

    private void CopyRowClick(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.CurrentItem is not LogEntry entry) return;
        SafeSetClipboard(FormatRow(entry));
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

    private static string? ExtractCellText(LogEntry entry, DataGridColumn column) => column.SortMemberPath switch
    {
        nameof(LogEntry.Timestamp) => entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
        nameof(LogEntry.Level) => entry.Level.ToString(),
        nameof(LogEntry.Message) => entry.Message,
        nameof(LogEntry.Category) => entry.Category,
        _ => null,
    };

    // Clipboard.SetText throws on the rare COM race when something else is
    // holding it open. The Log Viewer is a non-critical surface — swallow it
    // so a single failure doesn't tear down the dispatcher.
    private static void SafeSetClipboard(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}
