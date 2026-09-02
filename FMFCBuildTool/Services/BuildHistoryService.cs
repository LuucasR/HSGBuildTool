using System;
using System.Collections.Generic;
using System.Linq;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>
/// Keeps the record of finished builds, and turns it into an estimate for the next one.
/// </summary>
/// <remarks>
/// Backed by the app config, so it survives a restart. Capped, because config.json is
/// rewritten in full on every save and an unbounded list would eventually make that
/// expensive for no benefit — nobody looks at their four-hundredth-most-recent cook.
/// </remarks>
public sealed class BuildHistoryService
{
    /// <summary>Kept small enough that config.json stays a few tens of kilobytes.</summary>
    public const int MaxRecords = 200;

    /// <summary>
    /// How many past runs feed an estimate. A median over five is stable against the one
    /// cold cook that took three times as long, without lagging a real change in the project.
    /// </summary>
    private const int SampleSize = 5;

    private readonly AppConfig _config;

    public BuildHistoryService(AppConfig config)
    {
        _config = config;
    }

    /// <summary>A run finished and was recorded.</summary>
    public event Action<BuildRecord>? Recorded;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<BuildRecord> Records => _config.History;

    public void Record(BuildRecord record)
    {
        _config.History.Insert(0, record);

        if (_config.History.Count > MaxRecords)
            _config.History.RemoveRange(MaxRecords, _config.History.Count - MaxRecords);

        Recorded?.Invoke(record);
    }

    public void Clear()
    {
        _config.History.Clear();
    }

    /// <summary>
    /// Expected duration of the next run of this kind on this project, or null when there
    /// is nothing to go on. Only successful runs count: a build that failed after ten
    /// seconds says nothing about how long a real one takes.
    /// </summary>
    public TimeSpan? Estimate(string kind, string projectFile)
    {
        var samples = _config.History
            .Where(r => r.Kind == kind
                        && string.Equals(r.ProjectFile, projectFile, StringComparison.OrdinalIgnoreCase)
                        && r.Outcome == BuildOutcome.Succeeded
                        && r.DurationSeconds > 0)
            .Take(SampleSize)
            .Select(r => r.DurationSeconds)
            .OrderBy(s => s)
            .ToList();

        if (samples.Count == 0)
            return null;

        return TimeSpan.FromSeconds(samples[samples.Count / 2]);
    }

    public IReadOnlyList<BuildRecord> For(string projectFile)
        => _config.History
            .Where(r => string.Equals(r.ProjectFile, projectFile, StringComparison.OrdinalIgnoreCase))
            .ToList();
}
