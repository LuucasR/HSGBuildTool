using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The queue drives the real pages, so these use a stand-in page rather than launching
/// Unreal. What matters is the sequencing: order, what happens after a failure, and that a
/// step which cannot run is skipped rather than silently reported as a success.
/// </summary>
public class BuildQueueTests : IDisposable
{
    private readonly string _root;
    private readonly OutputService _output;

    public BuildQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        _output = new OutputService(Path.Combine(_root, "Logs"));
    }

    [Fact]
    public async Task Runs_the_enabled_steps_in_order()
    {
        var order = new List<string>();
        var (queue, steps) = Create(order);

        EnableAll(queue);

        await Run(queue);

        Assert.Equal(new[] { "package", "nav", "lighting" }, order);
        Assert.All(steps, s => Assert.Equal(BuildOutcome.Succeeded, s.LastOutcome));
    }

    [Fact]
    public async Task Skips_the_steps_that_are_not_selected()
    {
        var order = new List<string>();
        var (queue, _) = Create(order);

        queue.Steps.Single(s => s.Page.Kind == "nav").IsEnabled = true;

        await Run(queue);

        Assert.Equal(new[] { "nav" }, order);
    }

    /// <summary>
    /// Chaining exists to be walked away from, so by default one failure abandons the
    /// rest rather than pressing on for another half hour.
    /// </summary>
    [Fact]
    public async Task Stops_at_the_first_failure_by_default()
    {
        var order = new List<string>();
        var (queue, steps) = Create(order);

        steps[1].Outcome = BuildOutcome.Failed;

        EnableAll(queue);

        Assert.True(queue.StopOnFailure);

        await Run(queue);

        Assert.Equal(new[] { "package", "nav" }, order);
        Assert.Equal(QueueStepState.Failed, queue.Steps[1].State);
        Assert.Equal(QueueStepState.Skipped, queue.Steps[2].State);
    }

    [Fact]
    public async Task Carries_on_past_a_failure_when_told_to()
    {
        var order = new List<string>();
        var (queue, steps) = Create(order);

        steps[1].Outcome = BuildOutcome.Failed;

        EnableAll(queue);

        queue.StopOnFailure = false;

        await Run(queue);

        Assert.Equal(new[] { "package", "nav", "lighting" }, order);
        Assert.Equal(QueueStepState.Succeeded, queue.Steps[2].State);
    }

    /// <summary>Stop means stop the queue, not just the step in flight.</summary>
    [Fact]
    public async Task A_stopped_step_abandons_the_rest()
    {
        var order = new List<string>();
        var (queue, steps) = Create(order);

        steps[0].Outcome = BuildOutcome.Stopped;

        EnableAll(queue);

        queue.StopOnFailure = false;

        await Run(queue);

        Assert.Equal(new[] { "package" }, order);
        Assert.Equal(QueueStepState.Stopped, queue.Steps[0].State);
    }

    /// <summary>
    /// A step whose validation is failing would do nothing and report success, which is
    /// the worst possible outcome inside a chain.
    /// </summary>
    [Fact]
    public async Task A_step_that_cannot_run_is_skipped_not_run()
    {
        var order = new List<string>();
        var (queue, steps) = Create(order);

        steps[1].Validation = "Select at least one map.";

        EnableAll(queue);

        queue.StopOnFailure = false;

        await Run(queue);

        Assert.Equal(new[] { "package", "lighting" }, order);
        Assert.Equal(QueueStepState.Skipped, queue.Steps[1].State);
    }

    [Fact]
    public void The_selection_is_remembered()
    {
        var config = new AppConfig();
        var (queue, _) = Create(new List<string>(), config);

        queue.Steps.Single(s => s.Page.Kind == "nav").IsEnabled = true;
        queue.Steps.Single(s => s.Page.Kind == "lighting").IsEnabled = true;

        Assert.Equal(new[] { "nav", "lighting" }, config.QueueSteps);

        // A second session restores it.
        var (restored, _) = Create(new List<string>(), config);

        Assert.Equal(
            new[] { "nav", "lighting" },
            restored.Steps.Where(s => s.IsEnabled).Select(s => s.Page.Kind));
    }

    /// <summary>
    /// The order steps are ticked in is the order they run in. Anything else means picking
    /// Lighting first and Package second still cooks first, which is wrong whenever one
    /// step wants the output of another.
    /// </summary>
    [Fact]
    public async Task Runs_the_steps_in_the_order_they_were_picked()
    {
        var order = new List<string>();
        var (queue, _) = Create(order);

        queue.Steps.Single(s => s.Page.Kind == "lighting").IsEnabled = true;
        queue.Steps.Single(s => s.Page.Kind == "package").IsEnabled = true;

        // The cards themselves are the run order, not just the loop.
        Assert.Equal(new[] { "lighting", "package" }, queue.Steps.Take(2).Select(s => s.Page.Kind));
        Assert.Equal(new[] { 1, 2 }, queue.Steps.Take(2).Select(s => s.Position));

        await Run(queue);

        Assert.Equal(new[] { "lighting", "package" }, order);
    }

    /// <summary>Unticking drops a step out of the numbering and back below the picked ones.</summary>
    [Fact]
    public void Unpicking_a_step_sends_it_back_below_the_picked_ones()
    {
        var (queue, _) = Create(new List<string>());

        queue.Steps.Single(s => s.Page.Kind == "lighting").IsEnabled = true;
        queue.Steps.Single(s => s.Page.Kind == "package").IsEnabled = true;
        queue.Steps.Single(s => s.Page.Kind == "lighting").IsEnabled = false;

        Assert.Equal("package", queue.Steps[0].Page.Kind);
        Assert.Equal(1, queue.Steps[0].Position);

        Assert.Equal(0, queue.Steps.Single(s => s.Page.Kind == "lighting").Position);
        Assert.Equal("", queue.Steps.Single(s => s.Page.Kind == "lighting").PositionLabel);

        // Ticking it again puts it at the back, not back where it was.
        queue.Steps.Single(s => s.Page.Kind == "lighting").IsEnabled = true;

        Assert.Equal(new[] { "package", "lighting" }, queue.Steps.Take(2).Select(s => s.Page.Kind));
    }

    /// <summary>
    /// AppConfig.QueueSteps was already documented as ordered and written in order, but was
    /// read back with Contains, so the order died with the session.
    /// </summary>
    [Fact]
    public void The_pick_order_is_remembered()
    {
        var config = new AppConfig();
        var (queue, _) = Create(new List<string>(), config);

        queue.Steps.Single(s => s.Page.Kind == "lighting").IsEnabled = true;
        queue.Steps.Single(s => s.Page.Kind == "nav").IsEnabled = true;

        Assert.Equal(new[] { "lighting", "nav" }, config.QueueSteps);

        var (restored, _) = Create(new List<string>(), config);

        Assert.Equal(new[] { "lighting", "nav" }, restored.Steps.Take(2).Select(s => s.Page.Kind));
        Assert.Equal(new[] { 1, 2 }, restored.Steps.Take(2).Select(s => s.Position));

        // And a step picked in the restored session goes after the ones it restored.
        restored.Steps.Single(s => s.Page.Kind == "package").IsEnabled = true;

        Assert.Equal(new[] { "lighting", "nav", "package" }, config.QueueSteps);
    }

    /// <summary>Nothing selected means nothing to run; the button stays dead.</summary>
    [Fact]
    public void Cannot_run_an_empty_queue()
    {
        var (queue, _) = Create(new List<string>());

        Assert.False(queue.CanRun);

        queue.Steps[0].IsEnabled = true;

        Assert.True(queue.CanRun);
    }

    /// <summary>Ticks in the canonical order, so the list is not reordered underneath it.</summary>
    private static void EnableAll(BuildQueueViewModel queue)
    {
        foreach (var step in queue.Steps.ToList())
            step.IsEnabled = true;
    }

    private static Task Run(BuildQueueViewModel queue)
    {
        queue.RunQueueCommand.Execute(null);

        // AsyncRelayCommand is fire-and-forget; the fake pages complete synchronously, so
        // by the time the command returns the queue has finished.
        return Task.CompletedTask;
    }

    private (BuildQueueViewModel Queue, FakePage[] Steps) Create(List<string> order, AppConfig? config = null)
    {
        config ??= new AppConfig();

        var context = new BuildContext { ProjectFile = ValidProject() };

        var pages = new[]
        {
            new FakePage("package", order, _output),
            new FakePage("nav", order, _output),
            new FakePage("lighting", order, _output)
        };

        var queue = new BuildQueueViewModel(config, _output, new ProcessRunner(), context, pages);

        return (queue, pages);
    }

    private string ValidProject()
    {
        var file = Path.Combine(_root, "FMFC.uproject");

        if (!File.Exists(file))
            File.WriteAllText(file, "{ }");

        return file;
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

    /// <summary>A build page that records that it ran and reports a preset outcome.</summary>
    private sealed class FakePage : IBuildPage
    {
        private readonly List<string> _order;

        public FakePage(string kind, List<string> order, OutputService output)
        {
            Kind = kind;
            _order = order;

            LogExport = new LogExportViewModel(output);
        }

        public BuildOutcome Outcome { get; set; } = BuildOutcome.Succeeded;

        public string Validation { get; set; } = "";

        public string Kind { get; }

        public string RunButtonText => "RUN";

        public ICommand RunCommand { get; } = new RelayCommand(() => { });
        public ICommand StopCommand { get; } = new RelayCommand(() => { });
        public ICommand CopyCommandLineCommand { get; } = new RelayCommand(() => { });
        public ICommand SaveBatchFileCommand { get; } = new RelayCommand(() => { });
        public ICommand OpenLogFileCommand { get; } = new RelayCommand(() => { });
        public ICommand OpenLogFolderCommand { get; } = new RelayCommand(() => { });

        public LogExportViewModel LogExport { get; }

        public bool IsRunning => false;

        public bool CanRun => Validation.Length == 0;

        public bool IsProgressIndeterminate => true;

        public double Progress => 0;

        public string StatusText => "";

        public string ElapsedText => "";

        public string EstimateText => "";

        public string CommandPreview => "";

        public string ValidationMessage => Validation;

        public bool HasValidationMessage => Validation.Length > 0;

        public BuildOutcome? LastOutcome { get; private set; }

        public Task RunAsync()
        {
            _order.Add(Kind);
            LastOutcome = Outcome;

            return Task.CompletedTask;
        }
    }
}
