using System;
using System.IO;
using System.Linq;
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
