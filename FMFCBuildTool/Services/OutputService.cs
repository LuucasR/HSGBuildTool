using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>
/// The single sink for all build output: parses each line once, keeps a bounded
/// in-memory history, and mirrors everything to a per-run file on disk.
/// </summary>
/// <remarks>
/// Previously the same stream was rendered twice by two divergent consumers (a plain
/// TextBox in the shell and a RichTextBox in the Output page) and the buffer grew
/// without limit. Nothing was ever written to disk, so a build's output was gone the
/// moment the app closed.
/// </remarks>
public sealed class OutputService : IDisposable
{
    private const int MaxEntries = 200_000;
    private const int TrimChunk = 50_000;

    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = new();
    private readonly string _logDirectory;

    private StreamWriter? _writer;
    private bool _sessionActive;

    public OutputService(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    /// <summary>A single line was appended.</summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>History was cleared or trimmed; consumers must rebuild from <see cref="Snapshot"/>.</summary>
    public event Action? Reset;

    /// <summary>
    /// A build session opened or closed. These bracket one <em>logical</em> build, which is
    /// what the shell's status bar needs to time: <see cref="ProcessRunner.RunningChanged"/>
    /// fires once per process, and a navigation build spawns one process per map.
    /// </summary>
    public event Action? SessionStarted;

    public event Action? SessionEnded;

    public int TotalCount { get; private set; }

    public int WarningCount { get; private set; }

    public int ErrorCount { get; private set; }

    /// <summary>Where the per-run log files are written. Created on demand.</summary>
    public string LogDirectory => _logDirectory;

    /// <summary>Full path of the log file for the current run, or null when no run is active.</summary>
    public string? CurrentLogFile { get; private set; }

    /// <summary>When the current session began, or null when no session is active.</summary>
    public DateTime? SessionStartedAt { get; private set; }

    public bool IsSessionActive => _sessionActive;

    /// <summary>
    /// Warnings and errors in the current session alone. The cumulative counts above run
    /// for the life of the app, which is the wrong number to file against one build.
    /// </summary>
    public int SessionWarnings { get; private set; }

    public int SessionErrors { get; private set; }

    /// <summary>Branch and commit the current session was built from, when known.</summary>
    public GitInfo.Result SessionGit { get; private set; }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
    }

    /// <summary>
    /// How many buffered lines fall inside <paramref name="scope"/>. Asked before an export
    /// so "only errors" on a clean build says so instead of writing an empty file.
    /// </summary>
    public int CountFor(LogExportScope scope)
    {
        lock (_gate)
            return _entries.Count(e => scope.Includes(e.Severity));
    }

    /// <summary>
    /// Writes the buffered log to <paramref name="path"/>, keeping only the severities in
    /// <paramref name="scope"/> and their original order. Returns the number of lines written.
    /// </summary>
    /// <remarks>
    /// The buffer spans every task run since the app opened — <see cref="Clear"/> is the only
    /// thing that empties it — so after a queue of package, navigation and lighting this
    /// exports the errors of all three, not just the last step.
    /// </remarks>
    public int WriteFiltered(string path, LogExportScope scope)
    {
        var lines = Snapshot()
            .Where(e => scope.Includes(e.Severity))
            .Select(e => e.Text)
            .ToList();

        File.WriteAllLines(path, lines);

        return lines.Count;
    }

    /// <summary>Writes a line of process output, classified by <see cref="LogParser"/>.</summary>
    public void Write(string line) => Add(LogParser.Parse(line));

    /// <summary>Writes one of the tool's own messages, with an explicit severity.</summary>
    public void WriteTool(string message, LogSeverity severity = LogSeverity.Info)
        => Add(new LogEntry { Text = message, Severity = severity, Category = "FMFC" });

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();

            TotalCount = 0;
            WarningCount = 0;
            ErrorCount = 0;
        }

        Reset?.Invoke();
    }

    /// <summary>
    /// Opens a fresh log file for a run. <paramref name="label"/> identifies the kind of
    /// build ("package", "nav", "lighting") in the file name. <paramref name="projectFile"/>
    /// is optional and only used to stamp the log with the commit being built.
    /// </summary>
    public void BeginSession(string label, string projectName, string projectFile = "")
    {
        EndSession();

        SessionWarnings = 0;
        SessionErrors = 0;
        SessionGit = string.IsNullOrEmpty(projectFile) ? default : GitInfo.Read(projectFile);

        try
        {
            Directory.CreateDirectory(_logDirectory);

            var safeProject = Sanitise(projectName);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(_logDirectory, $"{stamp}-{safeProject}-{label}.log");

            _writer = new StreamWriter(path, append: false) { AutoFlush = true };

            CurrentLogFile = path;
        }
        catch (Exception ex)
        {
            _writer = null;
            CurrentLogFile = null;

            // Not fatal: the build still runs, only the on-disk copy is missing.
            Add(new LogEntry
            {
                Text = $"Could not open a log file in {_logDirectory}: {ex.Message}",
                Severity = LogSeverity.Warning,
                Category = "FMFC"
            });
        }

        // The session is open whether or not the file could be created — a build with no
        // on-disk copy is still a build, and the status bar still has to time it.
        SessionStartedAt = DateTime.Now;
        _sessionActive = true;

        // Stamped first, so months later the log says which commit produced this build.
        if (SessionGit.HasValue)
            WriteTool($"Source: {SessionGit.Label}");

        SessionStarted?.Invoke();
    }

    public void EndSession()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }

        if (!_sessionActive)
            return;

        _sessionActive = false;

        SessionEnded?.Invoke();
    }

    /// <summary>
    /// Opens the current run's log in the shell, falling back to the folder when there is
    /// no log yet (nothing has been built) or the file has since been pruned.
    /// </summary>
    public void OpenCurrentLogFile()
    {
        var path = CurrentLogFile;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            OpenLogFolder();
            return;
        }

        Launch(path);
    }

    /// <summary>Opens the log folder, creating it first so this works before any build.</summary>
    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
        }
        catch (Exception ex)
        {
            WriteTool($"Could not create {_logDirectory}: {ex.Message}", LogSeverity.Warning);
            return;
        }

        Launch(_logDirectory);
    }

    private void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WriteTool($"Could not open {target}: {ex.Message}", LogSeverity.Warning);
        }
    }

    /// <summary>Deletes log files older than <paramref name="retentionDays"/>.</summary>
    public void PruneLogs(int retentionDays)
    {
        if (retentionDays <= 0)
            return;

        try
        {
            if (!Directory.Exists(_logDirectory))
                return;

            var cutoff = DateTime.Now.AddDays(-retentionDays);

            foreach (var file in Directory.EnumerateFiles(_logDirectory, "*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // Housekeeping only — never let it interfere with a build.
        }
    }

    private void Add(LogEntry entry)
    {
        var trimmed = false;

        lock (_gate)
        {
            _entries.Add(entry);

            TotalCount++;

            if (entry.Severity == LogSeverity.Warning)
            {
                WarningCount++;

                if (_sessionActive)
                    SessionWarnings++;
            }
            else if (entry.Severity == LogSeverity.Error)
            {
                ErrorCount++;

                if (_sessionActive)
                    SessionErrors++;
            }

            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, TrimChunk);
                trimmed = true;
            }

            try
            {
                _writer?.WriteLine(entry.Text);
            }
            catch
            {
                // Disk full / file locked: keep the in-memory log working.
            }
        }

        if (trimmed)
            Reset?.Invoke();
        else
            EntryAdded?.Invoke(entry);
    }

    private static string Sanitise(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "project";

        return string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }

    public void Dispose() => EndSession();
}
