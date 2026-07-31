using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private bool _showInfo = true;
    private bool _showWarnings = true;
    private bool _showErrors = true;
    private bool _autoScroll = true;
    private string _search = "";

    public OutputViewModel(OutputService output)
    {
        _output = output;

        ClearCommand = new RelayCommand(Clear);
        OpenLogFileCommand = new RelayCommand(OpenLogFile, () => File.Exists(_output.CurrentLogFile ?? ""));
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        CopyVisibleCommand = new RelayCommand(CopyVisible);

        // Engine output arrives faster than the UI can render line by line, and from a
        // background thread. Batch it and flush on a timer.
        _output.EntryAdded += entry => _pending.Enqueue(entry);
        _output.Reset += OnServiceReset;

        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    public RangeObservableCollection<LogEntry> Lines { get; } = new();

    public ICommand ClearCommand { get; }
    public ICommand OpenLogFileCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand CopyVisibleCommand { get; }

    /// <summary>Raised after a flush when the view should scroll to the newest line.</summary>
    public event Action? ScrollToEndRequested;

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

    public string CurrentLogFile => _output.CurrentLogFile ?? "";

    public string EmptyMessage => TotalCount == 0
        ? "No output yet. Start a build to see engine output here."
        : "No lines match the current filter.";

    private void Flush()
    {
        if (_pending.IsEmpty)
            return;

        var batch = new List<LogEntry>();

        while (_pending.TryDequeue(out var entry))
        {
            if (Passes(entry))
                batch.Add(entry);
        }

        if (batch.Count > 0)
        {
            Lines.AddRange(batch);

            if (Lines.Count > MaxVisible)
                Lines.RemoveFirst(Lines.Count - MaxVisible);
        }

        RaiseCounts();

        if (AutoScroll && batch.Count > 0)
            ScrollToEndRequested?.Invoke();
    }

    private void OnServiceReset()
    {
        // Fired on clear and on history trim; both need a full rebuild from the snapshot.
        Application.Current?.Dispatcher.Invoke(Rebuild);
    }

    private void Rebuild()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        var visible = _output.Snapshot().Where(Passes).ToList();

        if (visible.Count > MaxVisible)
            visible = visible.Skip(visible.Count - MaxVisible).ToList();

        Lines.Reset(visible);

        RaiseCounts();

        if (AutoScroll)
            ScrollToEndRequested?.Invoke();
    }

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

    private void OpenLogFile()
    {
        var path = _output.CurrentLogFile;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            OpenLogFolder();
            return;
        }

        Launch(path);
    }

    private void OpenLogFolder()
    {
        var folder = Path.GetDirectoryName(_output.CurrentLogFile);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            folder = LogFolderFallback;

        if (Directory.Exists(folder))
            Launch(folder);
        else
            _output.WriteTool("No log folder yet — it is created when the first build starts.", LogSeverity.Warning);
    }

    /// <summary>Set by the shell so "Open folder" works before any build has run.</summary>
    public string LogFolderFallback { get; set; } = "";

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

    private void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not open {target}: {ex.Message}", LogSeverity.Warning);
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(CurrentLogFile));
        OnPropertyChanged(nameof(EmptyMessage));

        (OpenLogFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
