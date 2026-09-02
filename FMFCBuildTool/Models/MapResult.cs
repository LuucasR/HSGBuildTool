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

    public string DurationText => _durationSeconds <= 0
        ? ""
        : TimeSpan.FromSeconds(_durationSeconds).ToString(@"mm\:ss");

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
