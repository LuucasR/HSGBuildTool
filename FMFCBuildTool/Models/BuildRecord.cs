using System;

namespace FMFCBuildTool.Models;

public enum BuildOutcome
{
    Succeeded,
    Failed,
    Stopped
}

/// <summary>
/// One finished run, kept in the app config so the tool can answer "what did I build,
/// when, how long did it take, and where is the log".
/// </summary>
/// <remarks>
/// Also the source of the estimate shown before a build starts: a cook that took twelve
/// minutes last time will take roughly twelve minutes this time, and that is far more
/// useful than a progress bar that cannot know.
/// </remarks>
public sealed class BuildRecord
{
    /// <summary>"package", "nav" or "lighting" — the same label used for the log file.</summary>
    public string Kind { get; set; } = "";

    public string ProjectFile { get; set; } = "";

    public string ProjectName { get; set; } = "";

    /// <summary>Preset name for a package build, quality for lighting, empty otherwise.</summary>
    public string Detail { get; set; } = "";

    public DateTime StartedAt { get; set; }

    public double DurationSeconds { get; set; }

    public BuildOutcome Outcome { get; set; }

    public int ExitCode { get; set; }

    public string LogFile { get; set; } = "";

    public int Warnings { get; set; }

    public int Errors { get; set; }

    /// <summary>Branch and short commit of the project at build time, when it is a git work tree.</summary>
    public string GitBranch { get; set; } = "";

    public string GitCommit { get; set; } = "";

    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);

    public string DurationText => Duration.ToString(@"hh\:mm\:ss");

    public string KindLabel => Kind switch
    {
        "package" => "Package",
        "nav" => "Navigation",
        "lighting" => "Lighting",
        _ => Kind
    };

    /// <summary>Branch@commit, or empty when the project is not under git.</summary>
    public string GitLabel => string.IsNullOrEmpty(GitCommit)
        ? ""
        : string.IsNullOrEmpty(GitBranch) ? GitCommit : $"{GitBranch}@{GitCommit}";
}
