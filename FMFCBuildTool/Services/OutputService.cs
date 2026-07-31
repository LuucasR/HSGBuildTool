using System;
using System.Collections.Generic;
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

    public OutputService(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    /// <summary>A single line was appended.</summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>History was cleared or trimmed; consumers must rebuild from <see cref="Snapshot"/>.</summary>
    public event Action? Reset;

    public int TotalCount { get; private set; }

    public int WarningCount { get; private set; }

    public int ErrorCount { get; private set; }

    /// <summary>Full path of the log file for the current run, or null when no run is active.</summary>
    public string? CurrentLogFile { get; private set; }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
            return _entries.ToArray();
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
    /// build ("package", "nav", "lighting") in the file name.
    /// </summary>
    public void BeginSession(string label, string projectName)
    {
        EndSession();

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
    }

    public void EndSession()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
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
                WarningCount++;
            else if (entry.Severity == LogSeverity.Error)
                ErrorCount++;

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
