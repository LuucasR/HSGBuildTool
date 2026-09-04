using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// Exercises a real run end to end — a live process, the elapsed clock and Stop —
/// without going anywhere near an actual Unreal build.
/// </summary>
/// <remarks>
/// The "editor" here is ping, which is guaranteed present, takes a few predictable
/// seconds, and writes nothing. What is being tested is the page's own machinery:
/// that the clock ticks while a build runs rather than only reporting at the end (the
/// bug this page had), that Stop is live only while there is something to stop, and
/// that killing a run leaves the duration on screen.
/// </remarks>
public class BuildRunStateTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectFile;
    private readonly OutputService _output;

    public BuildRunStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        var maps = Path.Combine(_root, "Content", "Maps");

        Directory.CreateDirectory(maps);

        _projectFile = Path.Combine(_root, "FMFC.uproject");

        File.WriteAllText(_projectFile, "{ \"EngineAssociation\": \"5.4\" }");
        File.WriteAllText(Path.Combine(maps, "L_Arena.umap"), "");

        _output = new OutputService(Path.Combine(_root, "Logs"));
    }

    [Fact]
    public void The_clock_runs_and_stop_goes_live_while_a_build_is_in_flight()
    {
        Sta.Run(async () =>
        {
            var page = await StartAsync();

            Assert.True(page.IsRunning);
            Assert.True(page.StopCommand.CanExecute(null));

            // Ticks once a second, so a moment in it must read as a real duration and not
            // as the empty string it used to show for the whole run.
            await PumpAsync(TimeSpan.FromMilliseconds(1400));

            Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", page.ElapsedText);

            page.StopCommand.Execute(null);

            await WaitUntilAsync(() => !page.IsRunning, TimeSpan.FromSeconds(10));

            Assert.False(page.IsRunning);
            Assert.False(page.StopCommand.CanExecute(null));

            // The final duration survives the run rather than being blanked.
            Assert.Matches(@"^\d{2}:\d{2}:\d{2}$", page.ElapsedText);
            Assert.Contains("Stop", page.StatusText, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// The shell's status bar times off session events, so they have to bracket the whole
    /// run — including a run that is stopped part way through.
    /// </summary>
    [Fact]
    public void A_run_opens_and_closes_exactly_one_session()
    {
        Sta.Run(async () =>
        {
            var started = 0;
            var ended = 0;

            _output.SessionStarted += () => started++;
            _output.SessionEnded += () => ended++;

            var page = await StartAsync();

            Assert.Equal(1, started);
            Assert.Equal(0, ended);

            page.StopCommand.Execute(null);

            await WaitUntilAsync(() => !page.IsRunning, TimeSpan.FromSeconds(10));

            Assert.Equal(1, started);
            Assert.Equal(1, ended);
        });
    }

    /// <summary>Stopping writes a log file the page's "Log file" button can then open.</summary>
    [Fact]
    public async Task A_run_leaves_a_log_file_behind()
    {
        TestCommandletPage? page = null;

        Sta.Run(async () =>
        {
            page = await StartAsync();

            page.StopCommand.Execute(null);

            await WaitUntilAsync(() => !page.IsRunning, TimeSpan.FromSeconds(10));
        });

        Assert.NotNull(page);
        Assert.True(File.Exists(_output.CurrentLogFile));

        await Task.CompletedTask;
    }

    private async Task<TestCommandletPage> StartAsync()
    {
        var context = new BuildContext { ProjectFile = _projectFile };

        context.Engine = new EnginePaths
        {
            Root = _root,
            RunUAT = "unused",
            BuildBat = "unused",

            // Long enough that the assertions land while it is genuinely still running.
            EditorCmd = Path.Combine(Environment.SystemDirectory, "PING.EXE")
        };

        var config = new AppConfig();
        var page = new TestCommandletPage(context, new ProcessRunner(), _output, config, new BuildHistoryService(config));

        await page.OnProjectChangedAsync();

        page.MapSelection.SelectAllCommand.Execute(null);

        Assert.False(page.HasValidationMessage);
        Assert.True(page.RunCommand.CanExecute(null));

        page.RunCommand.Execute(null);

        await WaitUntilAsync(() => page.IsRunning, TimeSpan.FromSeconds(10));

        return page;
    }

    /// <summary>Keeps the dispatcher pumping, so DispatcherTimer ticks actually arrive.</summary>
    private static async Task PumpAsync(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;

        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(25);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(25);
        }
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

    /// <summary>A commandlet page whose "commandlet" is a harmless four-second ping.</summary>
    private sealed class TestCommandletPage : CommandletPageViewModel
    {
        public TestCommandletPage(
            BuildContext context,
            ProcessRunner runner,
            OutputService output,
            AppConfig config,
            BuildHistoryService history)
            : base(context, runner, output, config, history)
        {
        }

        public override string Kind => "nav";

        public override string RunButtonText => "RUN TEST";

        protected override string ActionName => "Test build";

        protected override IReadOnlyList<string> ArgumentsFor(string map)
            => new[] { "-n", "4", "127.0.0.1" };

        protected override IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps)
            => maps.Count == 0 ? new[] { "Select at least one map." } : Array.Empty<string>();
    }
}
