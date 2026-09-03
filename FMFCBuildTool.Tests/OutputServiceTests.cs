using System;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

public class OutputServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _logs;
    private readonly OutputService _output;

    public OutputServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));
        _logs = Path.Combine(_root, "Logs");

        Directory.CreateDirectory(_root);

        _output = new OutputService(_logs);
    }

    /// <summary>
    /// The build pages open the log folder straight off the service, so it has to say
    /// where that is. Previously only the shell knew, through a fallback property it set
    /// on the log view-model.
    /// </summary>
    [Fact]
    public void Exposes_its_log_directory()
    {
        Assert.Equal(_logs, _output.LogDirectory);
    }

    /// <summary>
    /// Session events bracket one logical build. The status bar times off them, because
    /// ProcessRunner.RunningChanged fires once per process and a navigation build starts
    /// one process per map.
    /// </summary>
    [Fact]
    public void Begin_and_end_session_raise_matching_events()
    {
        var started = 0;
        var ended = 0;

        _output.SessionStarted += () => started++;
        _output.SessionEnded += () => ended++;

        _output.BeginSession("nav", "FMFC");

        Assert.Equal(1, started);
        Assert.Equal(0, ended);
        Assert.True(_output.IsSessionActive);
        Assert.NotNull(_output.SessionStartedAt);

        _output.EndSession();

        Assert.Equal(1, ended);
        Assert.False(_output.IsSessionActive);
    }

    /// <summary>Ending a session that never began must not fire a phantom SessionEnded.</summary>
    [Fact]
    public void Ending_without_a_session_raises_nothing()
    {
        var ended = 0;

        _output.SessionEnded += () => ended++;

        _output.EndSession();
        _output.EndSession();

        Assert.Equal(0, ended);
    }

    /// <summary>Starting a second session closes the first exactly once.</summary>
    [Fact]
    public void Starting_a_session_closes_the_previous_one()
    {
        var started = 0;
        var ended = 0;

        _output.SessionStarted += () => started++;
        _output.SessionEnded += () => ended++;

        _output.BeginSession("package", "FMFC");
        _output.BeginSession("nav", "FMFC");

        Assert.Equal(2, started);
        Assert.Equal(1, ended);

        _output.EndSession();

        Assert.Equal(2, ended);
    }

    [Fact]
    public void Begin_session_writes_a_labelled_log_file()
    {
        _output.BeginSession("nav", "FMFC");
        _output.WriteTool("hello");
        _output.EndSession();

        var file = Directory.EnumerateFiles(_logs, "*.log").Single();

        Assert.EndsWith("-FMFC-nav.log", file);
        Assert.Contains("hello", File.ReadAllText(file));
    }

    /// <summary>
    /// The point of the export: hand someone the handful of lines that matter out of a
    /// couple of hundred thousand, without copying the file and pruning it by hand.
    /// </summary>
    [Fact]
    public void Write_filtered_keeps_only_the_chosen_severities()
    {
        Seed();

        var path = Path.Combine(_root, "errors.log");

        Assert.Equal(2, _output.WriteFiltered(path, LogExportScope.ErrorsOnly));
        Assert.Equal(new[] { "boom", "worse" }, File.ReadAllLines(path));
    }

    /// <summary>Original order, so the export still reads like the build that produced it.</summary>
    [Fact]
    public void Write_filtered_keeps_the_original_order()
    {
        Seed();

        var path = Path.Combine(_root, "issues.log");

        Assert.Equal(4, _output.WriteFiltered(path, LogExportScope.WarningsAndErrors));
        Assert.Equal(new[] { "careful", "boom", "also careful", "worse" }, File.ReadAllLines(path));
    }

    [Fact]
    public void Write_filtered_everything_keeps_the_quiet_lines_too()
    {
        Seed();

        var path = Path.Combine(_root, "full.log");

        Assert.Equal(6, _output.WriteFiltered(path, LogExportScope.Everything));
        Assert.Equal(6, File.ReadAllLines(path).Length);
    }

    /// <summary>
    /// What the export menu asks before opening a save dialog, so "errors only" on a clean
    /// build says so rather than writing an empty file.
    /// </summary>
    [Fact]
    public void Counts_the_lines_an_export_would_keep()
    {
        Seed();

        Assert.Equal(6, _output.CountFor(LogExportScope.Everything));
        Assert.Equal(4, _output.CountFor(LogExportScope.WarningsAndErrors));
        Assert.Equal(2, _output.CountFor(LogExportScope.ErrorsOnly));
        Assert.Equal(2, _output.CountFor(LogExportScope.WarningsOnly));

        _output.Clear();

        Assert.Equal(0, _output.CountFor(LogExportScope.ErrorsOnly));
    }

    /// <summary>Two warnings, two errors, one info and one verbose, in that interleaving.</summary>
    private void Seed()
    {
        _output.WriteTool("starting");
        _output.WriteTool("careful", LogSeverity.Warning);
        _output.WriteTool("boom", LogSeverity.Error);
        _output.WriteTool("also careful", LogSeverity.Warning);
        _output.WriteTool("worse", LogSeverity.Error);
        _output.WriteTool("chatter", LogSeverity.Verbose);
    }

    public void Dispose()
    {
        _output.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
