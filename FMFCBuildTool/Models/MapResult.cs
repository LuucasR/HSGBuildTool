using System;
using FMFCBuildTool.Core;

namespace FMFCBuildTool.Models;

/// <summary>
/// How one map fared in a commandlet run.
/// </summary>
/// <remarks>
/// The summary used to be a single log line — "3 succeeded, 2 failed — L_Hub, L_Arena" —
/// which meant that to retry the two failures you re-ran all five and waited through the
/// three that had already worked. Per-map results make "retry the failed ones" possible.
/// </remarks>
public sealed class MapResult : ObservableObject
{
    private MapRunState _state = MapRunState.Pending;
    private double _durationSeconds;
    private int _exitCode;
    private DateTime? _finishedAt;

    public required string Map { get; init; }

    /// <summary>Leaf name, for a list that would otherwise be a column of shared prefixes.</summary>
    public string Name
    {
        get
        {
            var slash = Map.LastIndexOf('/');

            return slash >= 0 && slash < Map.Length - 1 ? Map[(slash + 1)..] : Map;
        }
    }

    public MapRunState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(StateLabel));
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (SetProperty(ref _durationSeconds, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }

    public int ExitCode
    {
        get => _exitCode;
        set => SetProperty(ref _exitCode, value);
    }

    /// <summary>
    /// When this map finished, or null while it is still pending or running. How long a
    /// map took answers a different question from when it was done — an overnight batch
    /// needs the second one to be worth anything the next morning.
    /// </summary>
    public DateTime? FinishedAt
    {
        get => _finishedAt;
        set
        {
            if (SetProperty(ref _finishedAt, value))
                OnPropertyChanged(nameof(FinishedAtText));
        }
    }

    public string DurationText => _durationSeconds <= 0
        ? ""
        : TimeSpan.FromSeconds(_durationSeconds).ToString(@"mm\:ss");

    /// <summary>
    /// Date and clock time, in the same shape the History page uses, with seconds because
    /// a fast map can start and finish inside the same minute as the one before it.
    /// </summary>
    public string FinishedAtText => _finishedAt is { } at
        ? at.ToString("yyyy-MM-dd HH:mm:ss")
        : "";

    public string StateLabel => State switch
    {
        MapRunState.Running => "Running",
        MapRunState.Succeeded => "OK",
        MapRunState.Failed => $"Failed ({ExitCode})",
        MapRunState.Skipped => "Skipped",
        _ => "Pending"
    };
}

public enum MapRunState
{
    Pending,
    Running,
    Succeeded,
    Failed,

    /// <summary>Never started, because the run was stopped before reaching it.</summary>
    Skipped
}
