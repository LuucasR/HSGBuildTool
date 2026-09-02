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
/// Per-map results and "retry failed" — the reason the pages stopped reporting a run as
/// one line of text. Runs real processes, but harmless ones: cmd /c exit with a chosen
/// code, so a map can be made to fail on demand.
/// </summary>
public class MapResultsTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectFile;
    private readonly OutputService _output;

    public MapResultsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        var maps = Path.Combine(_root, "Content", "Maps");

        Directory.CreateDirectory(maps);

        _projectFile = Path.Combine(_root, "FMFC.uproject");

        File.WriteAllText(_projectFile, "{ \"EngineAssociation\": \"5.4\" }");

        foreach (var name in new[] { "L_Arena", "L_Hub", "L_Test" })
            File.WriteAllText(Path.Combine(maps, name + ".umap"), "");

        _output = new OutputService(Path.Combine(_root, "Logs"));
    }

    [Fact]
    public void A_run_reports_every_map()
    {
        Sta.Run(async () =>
        {
            var page = await BuildAsync(failing: Array.Empty<string>());

            Assert.Equal(3, page.Results.Count);
            Assert.All(page.Results, r => Assert.Equal(MapRunState.Succeeded, r.State));

            Assert.Equal(0, page.FailedCount);
            Assert.Contains("3 succeeded", page.ResultsSummary);
            Assert.False(page.RetryFailedCommand.CanExecute(null));
        });
    }

    /// <summary>
    /// One failing map must not take the others down — that is the entire point of one
    /// process per map — and the failures have to be identifiable afterwards.
    /// </summary>
    [Fact]
    public void One_failing_map_does_not_stop_the_rest()
    {
        Sta.Run(async () =>
        {
            var page = await BuildAsync(failing: new[] { "/Game/Maps/L_Hub" });

            Assert.Equal(3, page.Results.Count);
            Assert.Equal(1, page.FailedCount);

            var failed = page.Results.Single(r => r.State == MapRunState.Failed);

            Assert.Equal("L_Hub", failed.Name);
            Assert.Equal(3, failed.ExitCode);

            Assert.Equal(2, page.Results.Count(r => r.State == MapRunState.Succeeded));
            Assert.Contains("1 failed", page.ResultsSummary);

            Assert.Equal(BuildOutcome.Failed, page.LastOutcome);
            Assert.True(page.RetryFailedCommand.CanExecute(null));
        });
    }

    /// <summary>
    /// Retrying re-runs only what failed. Before this, fixing one map out of five meant
    /// sitting through the four that had already worked.
    /// </summary>
    [Fact]
    public void Retry_runs_only_the_maps_that_failed()
    {
        Sta.Run(async () =>
        {
            var page = await BuildAsync(failing: new[] { "/Game/Maps/L_Hub" });

            Assert.Equal(1, page.FailedCount);

            // The map is "fixed" before the retry.
            page.Failing.Clear();

            page.RetryFailedCommand.Execute(null);

            await WaitUntilAsync(() => !page.IsRunning, TimeSpan.FromSeconds(15));

            Assert.Single(page.Results);
            Assert.Equal("L_Hub", page.Results[0].Name);
            Assert.Equal(MapRunState.Succeeded, page.Results[0].State);
            Assert.Equal(BuildOutcome.Succeeded, page.LastOutcome);
        });
    }

    /// <summary>
    /// A retry narrows the selection for that run only. If it were treated as an edit,
    /// one retry would permanently shrink "all six maps" down to "the one that broke",
    /// and the next full build would quietly do a sixth of the work.
    /// </summary>
    [Fact]
    public void Retrying_does_not_shrink_the_saved_preset()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();

            var page = await BuildAsync(new[] { "/Game/Maps/L_Hub" }, config);

            var preset = config.GetOrCreate(_projectFile).GetActiveCommandletPreset("nav");

            Assert.Equal(3, preset.Maps.Count);

            page.Failing.Clear();
            page.RetryFailedCommand.Execute(null);

            await WaitUntilAsync(() => !page.IsRunning, TimeSpan.FromSeconds(15));

            Assert.Equal(3, preset.Maps.Count);
            Assert.Equal(3, page.MapSelection.SelectedMaps.Count);
        });
    }

    /// <summary>A finished run is filed in history, which is what feeds the estimate.</summary>
    [Fact]
    public void A_run_is_recorded_in_history()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var history = new BuildHistoryService(config);

            var page = await BuildAsync(Array.Empty<string>(), config, history);

            var record = Assert.Single(history.Records);

            Assert.Equal("nav", record.Kind);
            Assert.Equal(_projectFile, record.ProjectFile);
            Assert.Equal(BuildOutcome.Succeeded, record.Outcome);
            Assert.Contains("3 map(s)", record.Detail);
            Assert.Equal(_output.CurrentLogFile, record.LogFile);

            // And the estimate that history exists to produce shows up on the page.
            Assert.Contains("last time", page.EstimateText);
        });
    }

    private async Task<TestPage> BuildAsync(
        IReadOnlyCollection<string> failing,
        AppConfig? config = null,
        BuildHistoryService? history = null)
    {
        config ??= new AppConfig();
        history ??= new BuildHistoryService(config);

        var context = new BuildContext { ProjectFile = _projectFile };

        context.Engine = new EnginePaths
        {
            Root = _root,
            RunUAT = "unused",
            EditorCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe")
        };

        var page = new TestPage(context, new ProcessRunner(), _output, config, history);

        foreach (var map in failing)
            page.Failing.Add(map);

        await page.OnProjectChangedAsync();

        page.MapSelection.SelectAllCommand.Execute(null);

        page.RunCommand.Execute(null);

        await WaitUntilAsync(() => !page.IsRunning && page.Results.Count > 0, TimeSpan.FromSeconds(20));

        return page;
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

    /// <summary>
    /// A commandlet page whose "editor" is cmd.exe exiting with a chosen code, so a
    /// specific map can be made to fail without an engine anywhere in sight.
    /// </summary>
    private sealed class TestPage : CommandletPageViewModel
    {
        public TestPage(
            BuildContext context,
            ProcessRunner runner,
            OutputService output,
            AppConfig config,
            BuildHistoryService history)
            : base(context, runner, output, config, history)
        {
        }

        public HashSet<string> Failing { get; } = new();

        public override string Kind => "nav";

        public override string RunButtonText => "RUN TEST";

        protected override string ActionName => "Test build";

        protected override IReadOnlyList<string> ArgumentsFor(string map)
            => new[] { "/c", "exit", Failing.Contains(map) ? "3" : "0" };

        protected override IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps)
            => maps.Count == 0 ? new[] { "Select at least one map." } : Array.Empty<string>();
    }
}
