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

    // Safety valve while the tail is paused: trims are deferred so rows don't
    // crawl out from under the reader, but past 4x the cap they resume anyway.
    // Accept the UI crawling in a sustained flood; Entries is temporarily a
    // superset of the ring buffer while paused — deliberate, it reconverges on
    // resume. Don't "fix" either of those.
    private const int PausedHardCeiling = MaxDisplayedEntries * 4;

    // Debounce for the free-text search box so a fast typist doesn't pay a
    // full ICollectionView.Refresh per keystroke over 500 wrapped rows.
    private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(250);

    public const string AllTypesLabel = "(All types)";

    private readonly LogRingBuffer _buffer;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly FileLoggingSwitch _fileLoggingSwitch;
    private readonly IAppPaths _appPaths;
    private readonly ILogger<LogViewerViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly CollectionViewSource _viewSource;
    private readonly CollectionViewSource _classOptionsSource;
    private readonly TailFollowModel _tail = new();
    private readonly DispatcherTimer _searchDebounceTimer;
    private bool _suppressFilterRefresh;
    private bool _disposed;

    [ObservableProperty]
    private LogEventLevel _selectedLevel;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _logToDisk;

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

    // ---- Smart tail-follow state (decision logic lives in TailFollowModel;
    // these are read-only bindable mirrors the window and pill consume). ----

    /// <summary>True while the view stays pinned to the newest entry.</summary>
    public bool FollowTail => _tail.FollowTail;

    /// <summary>Raw entries added since the tail was paused (not filter-aware).</summary>
    public int PendingCount => _tail.PendingCount;

    /// <summary>
    /// Drives the "Paused" pill: only meaningful while Auto-scroll (the
    /// master switch) is checked — unchecked, the tail machinery is inert.
    /// </summary>
    public bool IsTailPaused => AutoScroll && !_tail.FollowTail;

    /// <summary>Raised when the tail resumes so the window can scroll to the end.</summary>
    public event Action? TailResumed;

    /// <summary>
    /// Raised whenever the entries view is refreshed (filter/search change)
    /// so the window can suppress one follow-state recomputation and restore
    /// the paused viewport anchor.
    /// </summary>
    public event Action? EntriesViewRefreshed;

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

        _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
        {
            Interval = SearchDebounceInterval,
        };
        _searchDebounceTimer.Tick += OnSearchDebounceElapsed;

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

    partial void OnAutoScrollChanged(bool value) => OnPropertyChanged(nameof(IsTailPaused));

    // ComboBox filters refresh immediately; the free-text search debounces
    // (a Refresh per keystroke re-filters all 500 rows and resets the view).
    partial void OnSelectedTypeFilterChanged(string value) => RefreshEntriesView();
    partial void OnSelectedClassOptionChanged(ClassFilterOption? value) => RefreshEntriesView();

    partial void OnMessageFilterChanged(string value)
    {
        if (_suppressFilterRefresh)
        {
            _searchDebounceTimer.Stop();
            return;
        }
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void OnSearchDebounceElapsed(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        RefreshEntriesView();
    }

    private void RefreshEntriesView()
    {
        if (_suppressFilterRefresh) return;
        EntriesView.Refresh();
        EntriesViewRefreshed?.Invoke();
    }

    // ---- Tail-follow transitions (called from the window's ScrollViewer /
    // selection / context-menu hooks, and from the pill's Resume button). ----

    /// <summary>
    /// A user-initiated scroll settled at the given position; recompute
    /// whether the view is at the tail.
    /// </summary>
    public void NotifyUserScroll(double verticalOffset, double viewportHeight, double extentHeight)
    {
        if (!_tail.OnUserScroll(verticalOffset, viewportHeight, extentHeight)) return;
        if (_tail.FollowTail)
        {
            // Scrolled back to the bottom: rejoin the tail (applies trims).
            ApplyResume();
        }
        else
        {
            SyncTailState();
        }
    }

    /// <summary>Explicit pause: a row was selected or a context menu opened.</summary>
    public void PauseTail()
    {
        if (_tail.Pause()) SyncTailState();
    }

    [RelayCommand]
    private void ResumeTail() => ApplyResume();

    private void ApplyResume()
    {
        // Apply the trims deferred while paused. If this removes the selected
        // item, WPF clears the selection — deliberate: the user chose to
        // rejoin the tail.
        var trims = _tail.Resume(Entries.Count, MaxDisplayedEntries);
        while (trims-- > 0)
        {
            Entries.RemoveAt(0);
        }
        SyncTailState();
        TailResumed?.Invoke();
    }

    private void SyncTailState()
    {
        OnPropertyChanged(nameof(FollowTail));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(IsTailPaused));
    }

    // Wired to "click the class name on a row to filter by it". Saves the
    // user from hunting through the Class dropdown when they've already
    // spotted the source they care about in the visible log lines.
    [RelayCommand]
    private void FilterByCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        var option = ClassOptions.FirstOrDefault(o =>
            string.Equals(o.Category, category, StringComparison.Ordinal));
        if (option is not null) SelectedClassOption = option;
    }

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

        // Nothing left to read, so a paused tail has nothing to anchor to:
        // clear the deferred-trim state and rejoin the tail.
        ApplyResume();
    }

    /// <summary>
    /// Explicit clear gesture (Esc in the search box): empty the search text
    /// and re-filter NOW, skipping the typing debounce — the user asked for
    /// the reset, so there is nothing to coalesce.
    /// </summary>
    public void ClearSearch()
    {
        _suppressFilterRefresh = true;
        try
        {
            MessageFilter = string.Empty;
        }
        finally
        {
            _suppressFilterRefresh = false;
        }
        _searchDebounceTimer.Stop();
        RefreshEntriesView();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        // Coalesce the three property changes into one view refresh.
        _suppressFilterRefresh = true;
        try
        {
            SelectedTypeFilter = AllTypesLabel;
            MessageFilter = string.Empty;
            SelectedClassOption = ClassFilterOption.All;
        }
        finally
        {
            _suppressFilterRefresh = false;
        }
        RefreshEntriesView();
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
        // Type filter.
        if (!string.Equals(SelectedTypeFilter, AllTypesLabel, StringComparison.Ordinal)
            && Enum.TryParse<LogEventLevel>(SelectedTypeFilter, out var levelFilter)
            && entry.Level != levelFilter)
        {
            return false;
        }

        // Message search. Searches both the message body and the exception
        // text so a user typing "403" or "TimeoutException" finds the right
        // row whether the term landed in the formatted message or the
        // attached exception.
        if (!string.IsNullOrEmpty(MessageFilter))
        {
            var inMessage = entry.Message?.IndexOf(MessageFilter, StringComparison.OrdinalIgnoreCase) >= 0;
            var inException = entry.Exception?.ToString().IndexOf(MessageFilter, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!inMessage && !inException) return false;
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

    private void OnEntryAdded(LogEntry entry)
    {
        if (_disposed) return;
        _dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            Entries.Add(entry);

            // Following: trim to the cap as always. Paused: defer trims so the
            // viewport stays anchored on what the user is reading (deferred
            // trims apply on resume), with a hard ceiling as a safety valve.
            var trims = _tail.OnEntryAdded(Entries.Count, MaxDisplayedEntries, PausedHardCeiling);
            while (trims-- > 0)
            {
                Entries.RemoveAt(0);
            }
            if (!_tail.FollowTail)
            {
                OnPropertyChanged(nameof(PendingCount));
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
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= OnSearchDebounceElapsed;
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
