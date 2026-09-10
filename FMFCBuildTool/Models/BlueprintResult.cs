using System;
using System.Collections.Generic;
using FMFCBuildTool.Core;

namespace FMFCBuildTool.Models;

/// <summary>
/// How one Blueprint fared in a run of the Compiler page's Blueprint steps.
/// </summary>
/// <remarks>
/// The shape of <see cref="MapResult"/>, and for the same reason: "3 Blueprints failed"
/// in the log is not something anyone can act on, and scrolling a 200,000-line editor
/// log looking for which three is exactly what this page exists to avoid.
///
/// Its own state enum rather than <see cref="MapRunState"/>, whose StateLabel renders
/// "Failed (exitCode)" — there is no per-Blueprint exit code, and the states here are a
/// different set: a Blueprint can be fixed by the refresh step, which no map ever is.
/// </remarks>
public sealed class BlueprintResult : ObservableObject
{
    /// <summary>How many error lines to keep. Enough to diagnose, few enough to render.</summary>
    public const int MaxRetainedErrors = 5;

    private BlueprintRunState _state = BlueprintRunState.Failed;
    private int _errorCount;
    private DateTime? _finishedAt;

    /// <summary>Package path, e.g. "/Game/Blueprints/BP_Door" — not the object path the log prints.</summary>
    public required string Path { get; init; }

    /// <summary>Leaf name, for a list that would otherwise be a column of shared prefixes.</summary>
    public string Name
    {
        get
        {
            var slash = Path.LastIndexOf('/');

            return slash >= 0 && slash < Path.Length - 1 ? Path[(slash + 1)..] : Path;
        }
    }

    /// <summary>
    /// The first few error lines attributed to this Blueprint. A path on its own says
    /// nothing about what broke, and the retained text is also what lets a human notice
    /// that a line was attributed to the wrong asset.
    /// </summary>
    public List<string> Errors { get; } = new();

    public string FirstError => Errors.Count > 0 ? Errors[0] : "";

    public BlueprintRunState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(StateLabel));
        }
    }

    /// <summary>Every error attributed here, including the ones past <see cref="MaxRetainedErrors"/>.</summary>
    public int ErrorCount
    {
        get => _errorCount;
        set => SetProperty(ref _errorCount, value);
    }

    public DateTime? FinishedAt
    {
        get => _finishedAt;
        set
        {
            if (SetProperty(ref _finishedAt, value))
                OnPropertyChanged(nameof(FinishedAtText));
        }
    }

    public string FinishedAtText => _finishedAt is { } at ? at.ToString("HH:mm:ss") : "";

    public string StateLabel => State switch
    {
        BlueprintRunState.Reloaded => "Reloaded",
        BlueprintRunState.Saved => "Saved",
        BlueprintRunState.StillFailing => "Still failing",
        BlueprintRunState.SaveFailed => "Save failed",
        BlueprintRunState.Unknown => "Unknown",
        _ => "Failed"
    };

    /// <summary>Adds an error line, keeping only the first few but counting them all.</summary>
    public void AddError(string text)
    {
        ErrorCount++;

        if (Errors.Count < MaxRetainedErrors)
        {
            Errors.Add(text);

            if (Errors.Count == 1)
                OnPropertyChanged(nameof(FirstError));
        }
    }
}

public enum BlueprintRunState
{
    /// <summary>Failed to compile in the CompileAllBlueprints step.</summary>
    Failed,

    /// <summary>Reloaded, recompiled and saved without an error.</summary>
    Reloaded,

    /// <summary>Reloaded and saved, but still logged errors while compiling.</summary>
    StillFailing,

    /// <summary>Reloaded cleanly but the asset could not be written — editor open, or a read-only file.</summary>
    SaveFailed,

    /// <summary>Compiled and saved, with nothing to report.</summary>
    Saved,

    /// <summary>
    /// The run could not say. A crash mid-asset, or output the tool could not read —
    /// never to be reported as "fine", which is the whole point of having this state.
    /// </summary>
    Unknown
}
