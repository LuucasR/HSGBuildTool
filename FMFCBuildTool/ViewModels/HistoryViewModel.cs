using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// What was built, when, for how long, and where the log went.
/// </summary>
/// <remarks>
/// Until now a finished build left nothing behind but a log file with a timestamp in its
/// name. This is the record that makes "how long does a full cook take on this project"
/// answerable — and it is the same data the pages use for their "~12:34 last time"
/// estimate.
/// </remarks>
public sealed class HistoryViewModel : ObservableObject
{
    private readonly BuildHistoryService _history;
    private readonly BuildContext _context;
    private readonly OutputService _output;

    private bool _thisProjectOnly = true;

    public HistoryViewModel(BuildHistoryService history, BuildContext context, OutputService output)
    {
        _history = history;
        _context = context;
        _output = output;

        OpenLogCommand = new RelayCommand(OpenLog);
        ClearCommand = new RelayCommand(Clear, () => Records.Count > 0);

        _history.Recorded += _ => Refresh();

        _context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BuildContext.ProjectFile))
                Refresh();
        };

        Refresh();
    }

    public ObservableCollection<BuildRecord> Records { get; } = new();

    public ICommand OpenLogCommand { get; }
    public ICommand ClearCommand { get; }

    /// <summary>Off shows every project, which is how you compare two branches of work.</summary>
    public bool ThisProjectOnly
    {
        get => _thisProjectOnly;
        set
        {
            if (SetProperty(ref _thisProjectOnly, value))
                Refresh();
        }
    }

    public bool IsEmpty => Records.Count == 0;

    public string EmptyMessage => _thisProjectOnly && _context.HasProject
        ? "No builds recorded for this project yet."
        : "No builds recorded yet.";

    public string SummaryText
    {
        get
        {
            if (Records.Count == 0)
                return "";

            var succeeded = Records.Count(r => r.Outcome == BuildOutcome.Succeeded);
            var total = TimeSpan.FromSeconds(Records.Sum(r => r.DurationSeconds));

            return $"{Records.Count} build(s) · {succeeded} succeeded · {total:hh\\:mm\\:ss} of build time";
        }
    }

    public void Refresh()
    {
        var source = _thisProjectOnly && _context.HasProject
            ? _history.For(_context.ProjectFile)
            : _history.Records;

        Records.Clear();

        foreach (var record in source)
            Records.Add(record);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(SummaryText));

        (ClearCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Opens the log for one past build. Log retention prunes files well before history
    /// forgets the run, so a missing file is normal and gets said plainly.
    /// </summary>
    private void OpenLog(object? parameter)
    {
        if (parameter is not BuildRecord record)
            return;

        if (string.IsNullOrEmpty(record.LogFile) || !File.Exists(record.LogFile))
        {
            _output.WriteTool(
                $"That log is gone — {record.StartedAt:yyyy-MM-dd HH:mm} is older than the log retention window.",
                LogSeverity.Warning);

            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(record.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not open {record.LogFile}: {ex.Message}", LogSeverity.Warning);
        }
    }

    private void Clear()
    {
        var confirm = MessageBox.Show(
            "Clear the whole build history? Estimates on the build pages will be lost too.",
            "FMFC Build Tool",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _history.Clear();

        Refresh();
    }
}
