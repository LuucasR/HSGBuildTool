using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// The one log viewer. Severity chips are independent toggles, so warnings and errors
/// can be shown together — the old single-select ComboBox could only show one at a time
/// (and rendered its own text invisible by setting Foreground to its Background).
/// </summary>
public sealed class OutputViewModel : ObservableObject
{
    /// <summary>Visible rows are capped well below the service's history so the UI stays responsive.</summary>
    private const int MaxVisible = 50_000;

    private readonly OutputService _output;
    private readonly AppConfig _config;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    /// <summary>
    /// The thread this view-model belongs to. Reset arrives from whichever thread trimmed
    /// the history, and the rebuild has to come back here. Captured rather than reached
    /// for through Application.Current, which is null outside a running app.
    /// </summary>
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    /// <summary>
    /// Problems list cap. Well past any run worth reading line by line, and low enough
    /// that a build which errors on every asset does not turn the panel into a second
    /// copy of the log.
    /// </summary>
    private const int MaxIssues = 5_000;

    private bool _showInfo = true;
    private bool _showWarnings = true;
    private bool _showErrors = true;
    private bool _autoScroll = true;
    private string _search = "";
    private LogEntry? _selectedIssue;

    public OutputViewModel(OutputService output, AppConfig config)
    {
        _output = output;
        _config = config;

        ClearCommand = new RelayCommand(Clear);

        // Opening the log belongs to the service that owns the file, so the build pages
        // can offer the same two actions without reaching into this view-model.
        OpenLogFileCommand = new RelayCommand(_output.OpenCurrentLogFile);
        OpenLogFolderCommand = new RelayCommand(_output.OpenLogFolder);

        CopyVisibleCommand = new RelayCommand(CopyVisible);
        CopyIssuesCommand = new RelayCommand(CopyIssues, () => Issues.Count > 0);

        NextIssueCommand = new RelayCommand(() => StepIssue(1), () => Issues.Count > 0);
        PreviousIssueCommand = new RelayCommand(() => StepIssue(-1), () => Issues.Count > 0);
        ToggleIssuesCommand = new RelayCommand(() => ShowIssues = !ShowIssues);

        // Engine output arrives faster than the UI can render line by line, and from a
        // background thread. Batch it and flush on a timer.
        _output.EntryAdded += entry => _pending.Enqueue(entry);
        _output.Reset += OnServiceReset;

        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    public RangeObservableCollection<LogEntry> Lines { get; } = new();

    /// <summary>
    /// Every warning and error, in order, regardless of the severity chips or the search
    /// box. Scrolling a 200,000-line log hunting for the one line that broke the build is
    /// the single worst thing about reading engine output; this is that list.
    /// </summary>
    public RangeObservableCollection<LogEntry> Issues { get; } = new();

    public ICommand ClearCommand { get; }
    public ICommand OpenLogFileCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand CopyVisibleCommand { get; }
    public ICommand CopyIssuesCommand { get; }
    public ICommand NextIssueCommand { get; }
    public ICommand PreviousIssueCommand { get; }
    public ICommand ToggleIssuesCommand { get; }

    /// <summary>Raised after a flush when the view should scroll to the newest line.</summary>
    public event Action? ScrollToEndRequested;

    /// <summary>Raised when the view should bring one specific line into view and select it.</summary>
    public event Action<LogEntry>? ScrollToEntryRequested;

    public int TotalCount => _output.TotalCount;
    public int WarningCount => _output.WarningCount;
    public int ErrorCount => _output.ErrorCount;

    public bool ShowInfo
    {
        get => _showInfo;
        set
        {
            if (SetProperty(ref _showInfo, value))
                Rebuild();
        }
    }

    public bool ShowWarnings
    {
        get => _showWarnings;
        set
        {
            if (SetProperty(ref _showWarnings, value))
                Rebuild();
        }
    }

    public bool ShowErrors
    {
        get => _showErrors;
        set
        {
            if (SetProperty(ref _showErrors, value))
                Rebuild();
        }
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
                Rebuild();
        }
    }

    public bool ShowIssues
    {
        get => _config.ShowLogIssues;
        set
        {
            if (_config.ShowLogIssues == value)
                return;

            _config.ShowLogIssues = value;

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Two-way with the problems list. Setting it jumps the log to that line, which is
    /// the whole point — the list is a table of contents, not a second place to read from.
    /// </summary>
    public LogEntry? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (!SetProperty(ref _selectedIssue, value))
                return;

            OnPropertyChanged(nameof(IssuePositionText));

            if (value is not null)
                JumpTo(value);
        }
    }

    /// <summary>"3 of 47" — where you are in the problems list.</summary>
    public string IssuePositionText
    {
        get
        {
            if (Issues.Count == 0)
                return "";

            var index = _selectedIssue is null ? -1 : Issues.IndexOf(_selectedIssue);

            return index < 0 ? $"{Issues.Count} issues" : $"{index + 1} of {Issues.Count}";
        }
    }

    public string CurrentLogFile => _output.CurrentLogFile ?? "";

    public string EmptyMessage => TotalCount == 0
        ? "No output yet. Start a build to see engine output here."
        : "No lines match the current filter.";

    private void Flush()
    {
        if (_pending.IsEmpty)
            return;

        var batch = new List<LogEntry>();
        var newIssues = new List<LogEntry>();

        while (_pending.TryDequeue(out var entry))
        {
            if (Passes(entry))
                batch.Add(entry);

            if (IsIssue(entry))
                newIssues.Add(entry);
        }

        if (batch.Count > 0)
        {
            Lines.AddRange(batch);

            if (Lines.Count > MaxVisible)
                Lines.RemoveFirst(Lines.Count - MaxVisible);
        }

        if (newIssues.Count > 0)
        {
            Issues.AddRange(newIssues);

            if (Issues.Count > MaxIssues)
                Issues.RemoveFirst(Issues.Count - MaxIssues);
        }

        RaiseCounts();

        if (AutoScroll && batch.Count > 0)
            ScrollToEndRequested?.Invoke();
    }

    private void OnServiceReset()
    {
        // Fired on clear and on history trim; both need a full rebuild from the snapshot.
        if (_dispatcher.CheckAccess())
            Rebuild();
        else
            _dispatcher.Invoke(Rebuild);
    }

    private void Rebuild()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        var snapshot = _output.Snapshot();
        var visible = snapshot.Where(Passes).ToList();

        if (visible.Count > MaxVisible)
            visible = visible.Skip(visible.Count - MaxVisible).ToList();

        Lines.Reset(visible);

        var issues = snapshot.Where(IsIssue).ToList();

        if (issues.Count > MaxIssues)
            issues = issues.Skip(issues.Count - MaxIssues).ToList();

        Issues.Reset(issues);

        SelectedIssue = null;

        RaiseCounts();

        if (AutoScroll)
            ScrollToEndRequested?.Invoke();
    }

    /// <summary>
    /// Warnings and errors only, and deliberately unaffected by the chips and the search
    /// box: turning off the Errors chip to read through the noise should not also empty
    /// the list you use to find your way back.
    /// </summary>
    private static bool IsIssue(LogEntry entry)
        => entry.Severity is LogSeverity.Warning or LogSeverity.Error;

    private bool Passes(LogEntry entry)
    {
        var severityMatches = entry.Severity switch
        {
            LogSeverity.Error => _showErrors,
            LogSeverity.Warning => _showWarnings,
            _ => _showInfo
        };

        if (!severityMatches)
            return false;

        return _search.Length == 0 ||
               entry.Text.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    private void Clear()
    {
        _output.Clear();
    }

    /// <summary>
    /// Moves through the problems list, wrapping. Bound to F8 / Shift+F8, so working
    /// through a failed build is a keystroke rather than a scroll hunt.
    /// </summary>
    private void StepIssue(int direction)
    {
        if (Issues.Count == 0)
            return;

        // Opening the panel on first use: jumping to an issue with the list hidden leaves
        // the user with no sense of how many more there are.
        ShowIssues = true;

        var current = _selectedIssue is null ? -1 : Issues.IndexOf(_selectedIssue);
        var next = current < 0
            ? (direction > 0 ? 0 : Issues.Count - 1)
            : (current + direction + Issues.Count) % Issues.Count;

        SelectedIssue = Issues[next];
    }

    /// <summary>
    /// Brings a line into view in the log. The severity chip for that line is turned on
    /// first, because jumping to a line the current filter hides would silently do nothing.
    /// </summary>
    private void JumpTo(LogEntry entry)
    {
        if (entry.Severity == LogSeverity.Error && !ShowErrors)
            ShowErrors = true;
        else if (entry.Severity == LogSeverity.Warning && !ShowWarnings)
            ShowWarnings = true;

        if (!Lines.Contains(entry))
        {
            // Still filtered out — by the search box, or trimmed from the visible window.
            return;
        }

        // Following the tail would drag the view straight back off the line just jumped to.
        AutoScroll = false;

        ScrollToEntryRequested?.Invoke(entry);
    }

    private void CopyIssues()
    {
        if (Issues.Count == 0)
            return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Issues.Select(i => i.Text)));

            _output.WriteTool($"Copied {Issues.Count} warning(s) and error(s) to the clipboard.");
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not copy to the clipboard: {ex.Message}", LogSeverity.Warning);
        }
    }

    private void CopyVisible()
    {
        if (Lines.Count == 0)
            return;

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, Lines.Select(l => l.Text)));

            _output.WriteTool($"Copied {Lines.Count} line(s) to the clipboard.");
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not copy to the clipboard: {ex.Message}", LogSeverity.Warning);
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(CurrentLogFile));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(IssuePositionText));

        (NextIssueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PreviousIssueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyIssuesCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
