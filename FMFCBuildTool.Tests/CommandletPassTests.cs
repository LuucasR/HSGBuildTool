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
/// A map whose turn takes more than one invocation — HLOD's delete-then-build pair.
/// Runs real but harmless processes: cmd /c appends a marker and exits with a chosen
/// code, so which passes actually ran can be read back off disk.
/// </summary>
public class CommandletPassTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectFile;
    private readonly string _marker;
    private readonly OutputService _output;

    public CommandletPassTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        var maps = Path.Combine(_root, "Content", "Maps");

        Directory.CreateDirectory(maps);

        _projectFile = Path.Combine(_root, "FMFC.uproject");
        _marker = Path.Combine(_root, "passes.txt");

        File.WriteAllText(_projectFile, "{ \"EngineAssociation\": \"5.4\" }");

        foreach (var name in new[] { "L_Arena", "L_Hub" })
            File.WriteAllText(Path.Combine(maps, name + ".umap"), "");

        _output = new OutputService(Path.Combine(_root, "Logs"));
    }

    [Fact]
    public void Every_pass_of_every_map_runs()
    {
        Sta.Run(async () =>
        {
            var page = await BuildAsync(failFirstPassOn: Array.Empty<string>());

            Assert.Equal(2, page.Results.Count);
            Assert.All(page.Results, r => Assert.Equal(MapRunState.Succeeded, r.State));

            // Two maps, two passes each.
            Assert.Equal(4, Markers().Count);
            Assert.Equal(2, Markers().Count(m => m == "one"));
            Assert.Equal(2, Markers().Count(m => m == "two"));
        });
    }

    /// <summary>
    /// The second pass is not attempted once the first fails. Building HLODs whose delete
    /// pass just failed produces a level nobody asked for, and both passes would be
    /// reported against the same map anyway.
    /// </summary>
    [Fact]
    public void A_failing_pass_abandons_the_rest_of_that_map_only()
    {
        Sta.Run(async () =>
        {
            var page = await BuildAsync(failFirstPassOn: new[] { "/Game/Maps/L_Arena" });

            var arena = page.Results.Single(r => r.Map == "/Game/Maps/L_Arena");
            var hub = page.Results.Single(r => r.Map == "/Game/Maps/L_Hub");

            Assert.Equal(MapRunState.Failed, arena.State);
            Assert.Equal(3, arena.ExitCode);

            // The other map still gets its full turn: one bad map does not cost the run.
            Assert.Equal(MapRunState.Succeeded, hub.State);

            // Arena's first pass, then both of Hub's — Arena's second never started.
            Assert.Equal(3, Markers().Count);

            // One failure, not two: the map is one row however many passes it took.
            Assert.Equal(1, page.FailedCount);
            Assert.Contains("1 failed", page.ResultsSummary);
        });
    }

    /// <summary>The preview has to show both commands, or it is not the run you are about to start.</summary>
    [Fact]
    public void The_preview_shows_every_pass()
    {
        Sta.Run(async () =>
        {
            var page = Create();

            await page.OnProjectChangedAsync();

            page.MapSelection.ApplySelection(new[] { "/Game/Maps/L_Arena" });

            var lines = page.CommandPreview.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, lines.Length);
            Assert.Contains("one", lines[0]);
            Assert.Contains("two", lines[1]);
        });
    }

    /// <summary>
    /// The .bat has to abort a map at its failing pass the way the UI does, while still
    /// carrying on to the next map and ending non-zero.
    /// </summary>
    [Fact]
    public void The_exported_script_abandons_a_map_at_its_failing_pass()
    {
        var script = BatchScriptWriter.ForEachMap(
            "HLOD build, preset \"Default\"",
            @"C:\Games\FMFC",
            @"C:\UE\UnrealEditor-Cmd.exe",
            new[] { "/Game/Maps/A", "/Game/Maps/B" },
            map => new[]
            {
                new CommandletPass("Delete HLODs", new[] { map, "-DeleteHLODs" }),
                new CommandletPass("Setup and build HLODs", new[] { map, "-SetupHLODs", "-BuildHLODs" })
            });

        // Skips this map's remaining passes...
        Assert.Contains("goto :map1_done", script);
        Assert.Contains(":map1_done", script);
        Assert.Contains("goto :map2_done", script);

        // ...but never the rest of the run, and still goes red for CI.
        Assert.Contains("FAILED: /Game/Maps/A - Delete HLODs", script);
        Assert.Contains("FAILED: /Game/Maps/B - Setup and build HLODs", script);
        Assert.Contains("exit /b 1", script);

        // The last pass has nothing left to skip to, so only the first pass jumps.
        Assert.Equal(1, Occurrences(script, "goto :map1_done"));
    }

    /// <summary>A single-pass page's script is exactly what it always was.</summary>
    [Fact]
    public void A_single_pass_script_gains_no_labels()
    {
        var script = BatchScriptWriter.ForEachMap(
            "Navigation build",
            @"C:\Games\FMFC",
            @"C:\UE\UnrealEditor-Cmd.exe",
            new[] { "/Game/Maps/A" },
            map => new[] { "-run=WorldPartitionBuilderCommandlet", $"-map={map}" });

        Assert.DoesNotContain("goto", script);
        Assert.DoesNotContain("_done", script);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private List<string> Markers()
        => File.Exists(_marker)
            ? File.ReadAllLines(_marker).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
            : new List<string>();

    private TwoPassPage Create()
    {
        var config = new AppConfig();
        var context = new BuildContext { ProjectFile = _projectFile };

        context.Engine = new EnginePaths
        {
            Root = _root,
            RunUAT = "unused",
            BuildBat = "unused",
            EditorCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe")
        };

        return new TwoPassPage(_marker, context, new ProcessRunner(), _output, config, new BuildHistoryService(config));
    }

    private async Task<TwoPassPage> BuildAsync(IReadOnlyCollection<string> failFirstPassOn)
    {
        var page = Create();

        foreach (var map in failFirstPassOn)
            page.FailingFirstPass.Add(map);

        await page.OnProjectChangedAsync();

        page.MapSelection.SelectAllCommand.Execute(null);
        page.RunCommand.Execute(null);

        await WaitUntilAsync(() => !page.IsRunning && page.Results.Count > 0, TimeSpan.FromSeconds(30));

        return page;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

            if (condition())
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException("The run did not finish in time.");
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
    /// A commandlet page whose map takes two invocations, each appending a marker line
    /// and exiting with a chosen code.
    /// </summary>
    private sealed class TwoPassPage : CommandletPageViewModel
    {
        private readonly string _marker;

        public TwoPassPage(
            string marker,
            BuildContext context,
            ProcessRunner runner,
            OutputService output,
            AppConfig config,
            BuildHistoryService history)
            : base(context, runner, output, config, history)
        {
            _marker = marker;
        }

        public HashSet<string> FailingFirstPass { get; } = new();

        public override string Kind => "hlod";

        public override string RunButtonText => "RUN TEST";

        protected override string ActionName => "Test build";

        protected override IReadOnlyList<CommandletPass> PassesFor(string map)
            => new[]
            {
                new CommandletPass("First", Step("one", FailingFirstPass.Contains(map) ? 3 : 0)),
                new CommandletPass("Second", Step("two", 0))
            };

        protected override IReadOnlyList<string> ArgumentsFor(string map) => Step("one", 0);

        protected override IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps)
            => maps.Count == 0 ? new[] { "Select at least one map." } : Array.Empty<string>();

        private IReadOnlyList<string> Step(string name, int exitCode)
            => new[] { "/c", "echo", $"{name}>>\"{_marker}\"", "&", "exit", exitCode.ToString() };
    }
}
