using System;
using System.Collections.Generic;
using System.Linq;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>Which of the two Blueprint steps a collector is reading.</summary>
public enum BlueprintPassKind
{
    /// <summary>The CompileAllBlueprints commandlet, read through its per-asset log lines.</summary>
    CompileAll,

    /// <summary>The generated Python, read through the markers it prints itself.</summary>
    Refresh
}

/// <summary>
/// Reads a Blueprint step's log as it arrives and works out which Blueprints failed.
/// </summary>
/// <remarks>
/// Fed from <see cref="OutputService.EntryAdded"/> rather than scanning
/// <see cref="OutputService.Snapshot"/> afterwards, and not for convenience: the service
/// caps its buffer at 200,000 entries and drops the oldest 50,000 when it overflows. A
/// full-project Blueprint compile can exceed that, so an after-the-fact scan would
/// silently lose the earliest failures — the ones nearest the start of an alphabetical
/// walk of /Game.
///
/// The rule this class exists to enforce is that <em>not knowing</em> is a distinct
/// answer from <em>nothing failed</em>. Unreal's log format is not a contract, so a
/// version that renames the per-asset line must produce an inconclusive run that says so
/// loudly, never a clean green one.
///
/// Attribution is deliberately conservative. Blaming an unrelated engine error on
/// whichever asset happened to be in flight sends someone to debug a Blueprint over a
/// missing shader cache, which is worse than reporting nothing at all.
/// </remarks>
public sealed class BlueprintFailureCollector
{
    private readonly object _gate = new();
    private readonly BlueprintPassKind _kind;
    private readonly bool _saving;
    private readonly Dictionary<string, BlueprintResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    private readonly HashSet<string> _listed = new(StringComparer.OrdinalIgnoreCase);

    private string _current = "";
    private int _attempted;
    private int _startupErrors;
    private bool _sawSummary;
    private bool _sawPythonOutput;
    private bool _inAssetList;
    private bool _completed;
    private BlueprintTotals? _totals;

    /// <param name="saving">
    /// Whether the refresh step was asked to save. Only changes what a clean result is
    /// called — "Refreshed" claims the asset was written, and it must not when it wasn't.
    /// </param>
    public BlueprintFailureCollector(BlueprintPassKind kind, bool saving = true)
    {
        _kind = kind;
        _saving = saving;
    }

    /// <summary>
    /// Takes one parsed log line.
    /// </summary>
    /// <remarks>
    /// <see cref="OutputService"/> raises its event outside its own lock, and
    /// <see cref="ProcessRunner"/> subscribes both stdout and stderr, which arrive on
    /// different threadpool threads — so this can be re-entered concurrently. It also
    /// keeps being called briefly after unsubscribing, because <c>-=</c> does not cancel
    /// an invocation already in flight, which is what <see cref="_completed"/> is for.
    /// </remarks>
    public void Observe(LogEntry entry)
    {
        lock (_gate)
        {
            if (_completed)
                return;

            // The tool's own WriteTool banners go through the same sink. Attributing our
            // "Step 2 of 3" line to a Blueprint would be absurd, and it would happen.
            if (entry.Category == "FMFC")
                return;

            if (entry.Category.Equals("LogPython", StringComparison.OrdinalIgnoreCase))
                _sawPythonOutput = true;

            if (_kind == BlueprintPassKind.CompileAll)
                ObserveCompileAll(entry);
            else
                ObserveRefresh(entry);
        }
    }

    /// <summary>
    /// Closes the pass and reports what it found. <paramref name="exitCode"/> is only used
    /// for the diagnosis: a Blueprint failure does not reliably change it, so it can never
    /// be read as "everything compiled".
    /// </summary>
    public BlueprintPassReport Complete(int exitCode)
    {
        lock (_gate)
        {
            _completed = true;

            // A BEGIN with no END means the editor died mid-asset. That Blueprint was not
            // fixed, and saying so is the point.
            if (_kind == BlueprintPassKind.Refresh && _current.Length > 0 && _results.TryGetValue(_current, out var open))
                open.State = BlueprintRunState.Unknown;

            var conclusive = IsConclusive();
            var rows = BuildRows(conclusive);

            return new BlueprintPassReport(
                conclusive,
                _attempted,
                rows,
                ForeignFailureCount(),
                _startupErrors,
                Diagnose(conclusive, exitCode),
                _totals);
        }
    }

    // ------------------------------------------------------------------ CompileAllBlueprints

    private void ObserveCompileAll(LogEntry entry)
    {
        // The -SimpleAssetList block: the commandlet's own list of the assets it had
        // something to say about. Errors *or* warnings, so it is a second opinion rather
        // than the answer — but it is the one signal that survives the per-asset lines
        // changing shape.
        if (entry.Text.Contains(BlueprintBuilder.AssetListStart, StringComparison.OrdinalIgnoreCase))
        {
            _inAssetList = true;

            return;
        }

        if (entry.Text.Contains(BlueprintBuilder.AssetListEnd, StringComparison.OrdinalIgnoreCase))
        {
            _inAssetList = false;

            return;
        }

        if (BlueprintBuilder.TotalsFromSummaryLine(entry.Text) is { } totals)
        {
            _totals = totals;
            _sawSummary = true;
            _inAssetList = false;

            return;
        }

        var asset = BlueprintBuilder.AssetFromLoadingLine(entry.Text);

        if (asset.Length > 0)
        {
            _current = asset;
            _attempted++;
            _inAssetList = false;

            return;
        }

        if (_inAssetList)
        {
            if (BlueprintBuilder.ListedAsset(entry.Text) is { Length: > 0 } listed)
                _listed.Add(listed);

            return;
        }

        // An asset that never got as far as compiling. It names itself, so this does not
        // depend on the "Loading and Compiling" line before it having been understood.
        if (BlueprintBuilder.AssetFromFailedToLoadLine(entry.Text) is { Length: > 0 } unloadable)
        {
            _current = unloadable;

            Row(_current).AddError(entry.Text);

            return;
        }

        if (!IsAttributableError(entry))
            return;

        // A load-time error that names its own asset belongs to that asset, not to
        // whichever Blueprint happened to be compiling when the dependency was pulled in.
        // This is the shape a changed struct leaves behind — "Unknown structure" against a
        // property path — so getting it on the right row is the point of the feature.
        if (BlueprintBuilder.AssetFromQuotedObjectPath(entry.Text) is { Length: > 0 } named &&
            !string.Equals(named, _current, StringComparison.OrdinalIgnoreCase))
        {
            Row(named).AddError(entry.Text);

            return;
        }

        // Everything before the first asset is start-up: module loading, the DDC, shader
        // compilation. None of it belongs to a Blueprint.
        if (_current.Length == 0)
        {
            _startupErrors++;

            return;
        }

        Row(_current).AddError(entry.Text);
    }

    // ------------------------------------------------------------------ refresh script

    private void ObserveRefresh(LogEntry entry)
    {
        if (Marker(entry.Text, BlueprintBuilder.BeginMarker) is { Length: > 0 } begun)
        {
            _sawPythonOutput = true;
            _current = begun;
            _attempted++;

            var row = Row(_current);
            row.State = BlueprintRunState.Unknown;

            return;
        }

        if (Marker(entry.Text, BlueprintBuilder.EndMarker) is { Length: > 0 } ended)
        {
            _sawPythonOutput = true;

            var parts = ended.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = parts.Length > 0 ? parts[0] : _current;
            var outcome = parts.Length > 1 ? parts[1] : BlueprintBuilder.ResultOk;

            var row = Row(path);
            row.State = StateFor(outcome, row.ErrorCount);
            row.FinishedAt = DateTime.Now;

            _current = "";

            return;
        }

        if (entry.Text.Contains(BlueprintBuilder.SummaryMarker, StringComparison.Ordinal))
        {
            _sawPythonOutput = true;
            _sawSummary = true;

            return;
        }

        if (!IsAttributableError(entry) || _current.Length == 0)
            return;

        Row(_current).AddError(entry.Text);
    }

    /// <summary>
    /// What the script reported, sharpened by what the log showed. The script cannot know
    /// whether the Blueprint compiled — <c>compile_blueprint()</c> returns None — so a
    /// clean "OK" over a run of compiler errors is still a failure.
    /// </summary>
    private BlueprintRunState StateFor(string outcome, int errorCount) => outcome switch
    {
        BlueprintBuilder.ResultLoadFailed => BlueprintRunState.Unknown,
        BlueprintBuilder.ResultException => BlueprintRunState.StillFailing,
        BlueprintBuilder.ResultSaveFailed => BlueprintRunState.SaveFailed,
        _ when errorCount > 0 => BlueprintRunState.StillFailing,
        _ => _saving ? BlueprintRunState.Reloaded : BlueprintRunState.Saved
    };

    /// <summary>The text after a marker, or empty when the line does not carry one.</summary>
    private static string Marker(string line, string marker)
    {
        var at = line.IndexOf(marker, StringComparison.Ordinal);

        return at < 0 ? "" : line[(at + marker.Length)..].Trim();
    }

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// Errors only — Blueprint compile warnings are ubiquitous, and counting them would
    /// make every project fail on the first run — and never from a category that has
    /// nothing to do with the asset being compiled.
    /// </summary>
    /// <remarks>
    /// Which Blueprint an error belongs to comes from the "Loading and Compiling" line
    /// before it, never from the error itself. The error does carry an asset, but as a
    /// filesystem path rather than a content path —
    /// <c>LogBlueprint: Error: [AssetLog] C:\Proj\Content\BP_Door.uasset: [Compiler] …</c> —
    /// so reading it would mean mapping mount points back to package roots, for an answer
    /// the position already gives.
    /// </remarks>
    private static bool IsAttributableError(LogEntry entry)
        => entry.Severity == LogSeverity.Error && !BlueprintBuilder.NoiseCategories.Contains(entry.Category);

    private BlueprintResult Row(string path)
    {
        if (_results.TryGetValue(path, out var existing))
            return existing;

        var row = new BlueprintResult { Path = path };

        _results[path] = row;
        _order.Add(path);

        return row;
    }

    /// <summary>
    /// True only when the step's output was actually understood.
    /// </summary>
    /// <remarks>
    /// For the commandlet, its closing "Compiling Completed with…" line is the real
    /// evidence: it is printed once, at the end, and only if the run got there. Reaching
    /// per-asset lines without it means the editor died partway, and a partial answer must
    /// not be presented as the whole one.
    /// </remarks>
    private bool IsConclusive() => _kind switch
    {
        BlueprintPassKind.CompileAll => _sawSummary && (_attempted > 0 || _totals is { Errors: 0, Warnings: 0 }),
        _ => _sawSummary
    };

    private IReadOnlyList<BlueprintResult> BuildRows(bool conclusive)
    {
        if (!conclusive)
        {
            // One row that says so, rather than an empty list a reader would take for a
            // clean bill of health.
            var unknown = new BlueprintResult { Path = "(unknown)", State = BlueprintRunState.Unknown };

            unknown.AddError(_kind == BlueprintPassKind.CompileAll
                ? "The commandlet's per-Blueprint output could not be read."
                : "The refresh script did not report a summary.");

            return new[] { unknown };
        }

        if (_kind == BlueprintPassKind.Refresh)
            return _order.Select(path => _results[path]).ToList();

        // Engine and plugin Blueprints are compiled too, and are not this project's to fix.
        return _order
            .Select(path => _results[path])
            .Where(row => row.ErrorCount > 0 && BlueprintBuilder.IsProjectContent(row.Path))
            .ToList();
    }

    private int ForeignFailureCount()
        => _kind == BlueprintPassKind.CompileAll
            ? _results.Values.Count(row => row.ErrorCount > 0 && !BlueprintBuilder.IsProjectContent(row.Path))
            : 0;

    /// <summary>
    /// What to tell the user when the numbers alone would mislead. Empty when the run
    /// speaks for itself.
    /// </summary>
    private string Diagnose(bool conclusive, int exitCode)
    {
        if (!conclusive)
        {
            if (_kind == BlueprintPassKind.Refresh && !_sawPythonOutput)
                return "The refresh script produced no output at all. The Python Editor Script plugin is most likely not enabled for this project.";

            return _kind == BlueprintPassKind.CompileAll
                ? "The commandlet never reported that it finished, so this run cannot say which Blueprints failed. Check the log."
                : "The refresh script did not finish, so this run cannot say which Blueprints were fixed. Check the log.";
        }

        var failures = _results.Values.Count(row => row.ErrorCount > 0);

        // The commandlet counted errors and the page could not say whose. Reporting an
        // empty list here would be the exact lie this class exists to prevent.
        if (_totals is { Errors: > 0 } counted && failures == 0)
        {
            var listed = _listed.Count > 0
                ? $" Its own asset list names {_listed.Count}: {string.Join(", ", _listed.Take(5))}."
                : "";

            return $"The commandlet reported {counted.Errors} error(s) but none could be attributed to a Blueprint.{listed} Check the log.";
        }

        if (exitCode != 0 && failures == 0)
            return $"The step exited with code {exitCode} but no failure could be attributed to a Blueprint. Check the log.";

        return "";
    }
}

/// <summary>
/// What one Blueprint step found.
/// </summary>
/// <param name="Conclusive">
/// False when the output could not be read. Everything else in this record is then
/// meaningless, and a caller that treats it as "nothing failed" has the bug this
/// whole type exists to prevent.
/// </param>
/// <param name="Attempted">Blueprints the step got as far as loading.</param>
/// <param name="Results">The rows worth showing: failures for the compile step, every asset for the refresh step.</param>
/// <param name="ForeignFailureCount">Failures in engine or plugin content, counted but not listed.</param>
/// <param name="StartupErrorCount">Errors logged before the first Blueprint, attributed to none.</param>
/// <param name="Diagnosis">A sentence to log when the numbers alone would mislead, otherwise empty.</param>
/// <param name="Totals">The commandlet's own tally, when it printed one.</param>
public sealed record BlueprintPassReport(
    bool Conclusive,
    int Attempted,
    IReadOnlyList<BlueprintResult> Results,
    int ForeignFailureCount,
    int StartupErrorCount,
    string Diagnosis,
    BlueprintTotals? Totals = null)
{
    /// <summary>Blueprints this run has not shown to be healthy. The refresh step works from these.</summary>
    public IReadOnlyList<BlueprintResult> Unresolved => Results
        .Where(row => row.State is not (BlueprintRunState.Reloaded or BlueprintRunState.Saved))
        .ToList();

    /// <summary>The paths to hand the refresh script.</summary>
    public IReadOnlyList<string> UnresolvedPaths => Unresolved
        .Where(row => BlueprintBuilder.IsProjectContent(row.Path))
        .Select(row => row.Path)
        .ToList();
}
