using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using AzureTray.Logging;
using Serilog.Core;
using Serilog.Events;

namespace AzureTray.ViewModels;

public sealed partial class LogViewerViewModel : ObservableObject, IDisposable
{
    // Trim the bound collection independently of the underlying buffer so the
    // window stays responsive even under a flood of events. Matches the
    // default buffer capacity so by default no entries are lost.
    private const int MaxDisplayedEntries = 500;

    public const string AllTypesLabel = "(All types)";

    private readonly LogRingBuffer _buffer;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly FileLoggingSwitch _fileLoggingSwitch;
    private readonly IAppPaths _appPaths;
    private readonly ILogger<LogViewerViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly CollectionViewSource _viewSource;
    private readonly CollectionViewSource _classOptionsSource;
    private bool _disposed;

    [ObservableProperty]
    private LogEventLevel _selectedLevel;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _logToDisk;

    // Timestamp filter — both bounds optional. When a date is set, the
    // paired time text refines it; an empty time defaults to the start
    // of day (From) or the end of day (To). When the date is null, the
    // bound is ignored entirely.
    [ObservableProperty]
    private DateTime? _fromDate;

    [ObservableProperty]
    private string _fromTime = string.Empty;

    [ObservableProperty]
    private DateTime? _toDate;

    [ObservableProperty]
    private string _toTime = string.Empty;

    // Type filter: list of strings including "(All types)" as the first
    // option. ComboBox binds to SelectedTypeFilter; MatchesFilter parses
    // it back to LogEventLevel.
    public IReadOnlyList<string> TypeFilterOptions { get; } = new[]
    {
        AllTypesLabel,
        nameof(LogEventLevel.Verbose),
        nameof(LogEventLevel.Debug),
        nameof(LogEventLevel.Information),
        nameof(LogEventLevel.Warning),
        nameof(LogEventLevel.Error),
        nameof(LogEventLevel.Fatal),
    };

    [ObservableProperty]
    private string _selectedTypeFilter = AllTypesLabel;

    [ObservableProperty]
    private string _messageFilter = string.Empty;

    // Class filter: grouped dropdown of distinct categories seen in the
    // buffer. SelectedClassOption is null when no filter; otherwise carries
    // both the full category (for matching) and the display label.
    public ObservableCollection<ClassFilterOption> ClassOptions { get; } = new();
    public ICollectionView ClassOptionsView { get; }

    [ObservableProperty]
    private ClassFilterOption? _selectedClassOption;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    // Filtered view bound by the DataGrid. The grid drives sorting through
    // column-header clicks; we drive filtering via the toolbar above.
    public ICollectionView EntriesView { get; }

    public IReadOnlyList<LogEventLevel> AvailableLevels { get; } = new[]
    {
        LogEventLevel.Verbose,
        LogEventLevel.Debug,
        LogEventLevel.Information,
        LogEventLevel.Warning,
        LogEventLevel.Error,
        LogEventLevel.Fatal,
    };

    public LogViewerViewModel(
        LogRingBuffer buffer,
        LoggingLevelSwitch levelSwitch,
        FileLoggingSwitch fileLoggingSwitch,
        IAppPaths appPaths,
        ILogger<LogViewerViewModel> logger)
    {
        _buffer = buffer;
        _levelSwitch = levelSwitch;
        _fileLoggingSwitch = fileLoggingSwitch;
        _appPaths = appPaths;
        _logger = logger;
        _dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("LogViewerViewModel requires a running WPF Application.");

        // CRITICAL ORDERING: initialise _viewSource + EntriesView BEFORE
        // any [ObservableProperty] setters fire, because their partial
        // OnXChanged methods all call EntriesView.Refresh(). Setting
        // SelectedLevel / SelectedClassOption / etc. before EntriesView
        // exists throws NRE inside the ctor and the Log Viewer window
        // never opens.

        foreach (var entry in _buffer.Snapshot())
        {
            Entries.Add(entry);
        }

        _viewSource = new CollectionViewSource { Source = Entries };
        _viewSource.Filter += OnViewFilter;
        EntriesView = _viewSource.View;

        // Seed the "(All classes)" sentinel option so the dropdown always
        // has a value selectable even before any logs arrive. Add it BEFORE
        // we populate per-category options so the "(All classes)" entry
        // sorts first.
        ClassOptions.Add(ClassFilterOption.All);
        foreach (var entry in _buffer.Snapshot())
        {
            EnsureClassOption(entry.Category);
        }

        // Grouped class dropdown — header shows the namespace root,
        // items show the full category. Sorted ascending.
        _classOptionsSource = new CollectionViewSource { Source = ClassOptions };
        _classOptionsSource.SortDescriptions.Add(new SortDescription(nameof(ClassFilterOption.SortKey), ListSortDirection.Ascending));
        _classOptionsSource.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ClassFilterOption.Group)));
        ClassOptionsView = _classOptionsSource.View;

        // Now safe to assign — every OnXChanged target is initialised.
        SelectedLevel = _levelSwitch.MinimumLevel;
        LogToDisk = _fileLoggingSwitch.Enabled;
        SelectedClassOption = ClassFilterOption.All;

        _buffer.EntryAdded += OnEntryAdded;
    }

    public string LogsDirectory => _appPaths.LogsDir;

    partial void OnSelectedLevelChanged(LogEventLevel value)
    {
        _levelSwitch.MinimumLevel = value;
    }

    partial void OnLogToDiskChanged(bool value)
    {
        _fileLoggingSwitch.Enabled = value;
        _logger.LogInformation("Log-to-disk {State}", value ? "enabled" : "disabled");
    }

    partial void OnFromDateChanged(DateTime? value) => EntriesView.Refresh();
    partial void OnFromTimeChanged(string value) => EntriesView.Refresh();
    partial void OnToDateChanged(DateTime? value) => EntriesView.Refresh();
    partial void OnToTimeChanged(string value) => EntriesView.Refresh();
    partial void OnSelectedTypeFilterChanged(string value) => EntriesView.Refresh();
    partial void OnMessageFilterChanged(string value) => EntriesView.Refresh();
    partial void OnSelectedClassOptionChanged(ClassFilterOption? value) => EntriesView.Refresh();

    [RelayCommand]
    private void ClearLog()
    {
        _buffer.Clear();
        Entries.Clear();
        // Keep "(All classes)" sentinel; drop the per-category entries
        // that were unique to the cleared logs.
        for (var i = ClassOptions.Count - 1; i >= 0; i--)
        {
            if (!ClassOptions[i].IsAll)
            {
                ClassOptions.RemoveAt(i);
            }
        }
        SelectedClassOption = ClassFilterOption.All;
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FromDate = null;
        FromTime = string.Empty;
        ToDate = null;
        ToTime = string.Empty;
        SelectedTypeFilter = AllTypesLabel;
        MessageFilter = string.Empty;
        SelectedClassOption = ClassFilterOption.All;
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(_appPaths.LogsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = _appPaths.LogsDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open logs folder {Path}", _appPaths.LogsDir);
            System.Windows.MessageBox.Show(
                $"Could not open logs folder:\n{_appPaths.LogsDir}\n\n{ex.Message}",
                "Log Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnViewFilter(object sender, FilterEventArgs e)
    {
        if (e.Item is not LogEntry entry)
        {
            e.Accepted = false;
            return;
        }
        e.Accepted = MatchesFilter(entry);
    }

    private bool MatchesFilter(LogEntry entry)
    {
        // Timestamp lower bound.
        if (FromDate is { } fromDate)
        {
            var from = CombineDateAndTime(fromDate, FromTime, endOfDay: false);
            if (entry.Timestamp < from) return false;
        }

        // Timestamp upper bound.
        if (ToDate is { } toDate)
        {
            var to = CombineDateAndTime(toDate, ToTime, endOfDay: true);
            if (entry.Timestamp > to) return false;
        }

        // Type filter.
        if (!string.Equals(SelectedTypeFilter, AllTypesLabel, StringComparison.Ordinal)
            && Enum.TryParse<LogEventLevel>(SelectedTypeFilter, out var levelFilter)
            && entry.Level != levelFilter)
        {
            return false;
        }

        // Message search.
        if (!string.IsNullOrEmpty(MessageFilter))
        {
            if (string.IsNullOrEmpty(entry.Message)) return false;
            if (entry.Message.IndexOf(MessageFilter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        // Class filter.
        if (SelectedClassOption is { IsAll: false } classFilter)
        {
            if (!string.Equals(entry.Category, classFilter.Category, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Combines the date part with the user-typed time. Accepts "HH:mm",
    // "HH:mm:ss", and "HH:mm:ss.fff". Falls back to start-of-day or
    // end-of-day when the time is unparseable.
    private static DateTimeOffset CombineDateAndTime(DateTime date, string timeText, bool endOfDay)
    {
        var basePart = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local);

        if (string.IsNullOrWhiteSpace(timeText))
        {
            return endOfDay
                ? new DateTimeOffset(basePart.AddDays(1).AddTicks(-1))
                : new DateTimeOffset(basePart);
        }

        if (TimeSpan.TryParse(timeText.Trim(), CultureInfo.InvariantCulture, out var parsed))
        {
            return new DateTimeOffset(basePart + parsed);
        }

        return endOfDay
            ? new DateTimeOffset(basePart.AddDays(1).AddTicks(-1))
            : new DateTimeOffset(basePart);
    }

    private void OnEntryAdded(LogEntry entry)
    {
        if (_disposed) return;
        _dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            Entries.Add(entry);
            while (Entries.Count > MaxDisplayedEntries)
            {
                Entries.RemoveAt(0);
            }
            EnsureClassOption(entry.Category);
        });
    }

    // Adds a new class option to the grouped dropdown when an
    // unseen category appears. The grouped ICollectionView refreshes
    // automatically as ClassOptions changes.
    private void EnsureClassOption(string? category)
    {
        if (string.IsNullOrEmpty(category)) return;
        for (var i = 0; i < ClassOptions.Count; i++)
        {
            if (string.Equals(ClassOptions[i].Category, category, StringComparison.Ordinal)) return;
        }
        ClassOptions.Add(ClassFilterOption.For(category));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer.EntryAdded -= OnEntryAdded;
        _viewSource.Filter -= OnViewFilter;
    }
}

// Entry in the grouped Class-filter dropdown. Group is the first two
// namespace segments (e.g. "AzureTray.Auth"), display label is the full
// category. The "(All classes)" sentinel uses its own group so it lands
// at the top of the grouped view.
public sealed record ClassFilterOption(string Group, string Category, string DisplayName, bool IsAll = false)
{
    public static ClassFilterOption All { get; } = new(
        Group: " ",   // leading space sorts first
        Category: string.Empty,
        DisplayName: "(All classes)",
        IsAll: true);

    public static ClassFilterOption For(string category)
    {
        // First two segments make a useful group ("AzureTray.Auth",
        // "AzureTray.Plugins", "Microsoft.Extensions", etc.). Falls
        // back to the whole category when it has < 2 segments.
        var dotIndex = category.IndexOf('.');
        if (dotIndex < 0) return new ClassFilterOption(category, category, category);
        var secondDot = category.IndexOf('.', dotIndex + 1);
        var group = secondDot < 0 ? category[..dotIndex] : category[..secondDot];
        return new ClassFilterOption(group, category, category);
    }

    // Sorting key keeps "(All classes)" first, then category alphabetical.
    public string SortKey => IsAll ? "\0" : Category;
}
