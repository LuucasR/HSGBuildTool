using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// Runs Package, Navigation and Lighting back to back.
/// </summary>
/// <remarks>
/// <see cref="ProcessRunner"/> is deliberately single-flight, so the three pages could
/// never overlap — which in practice meant sitting and waiting for a cook to finish in
/// order to click BUILD NAVIGATION, then waiting again for that. The queue is the missing
/// piece: pick the steps once, and the tool works through them.
///
/// It drives the pages themselves rather than re-implementing their builds, so a queued
/// run is identical to a manual one, down to the log file per step.
///
/// Steps run in the order they were picked, not in the order the shell happens to hand
/// the pages over. Ticking Lighting and then Package used to run Package first regardless,
/// which is wrong whenever one step wants the output of another.
/// </remarks>
public sealed class BuildQueueViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly OutputService _output;
    private readonly ProcessRunner _runner;

    private bool _isRunning;
    private string _statusText = "Ready";

    /// <summary>Stamps each pick so the order survives being unticked and ticked again.</summary>
    private int _pickCounter;

    /// <summary>A pick arrived mid-run; the cards are reordered once the run is over.</summary>
    private bool _resortPending;

    public BuildQueueViewModel(
        AppConfig config,
        OutputService output,
        ProcessRunner runner,
        BuildContext context,
        IReadOnlyList<IBuildPage> pages)
    {
        _config = config;
        _output = output;
        _runner = runner;

        Context = context;

        // The saved list is ordered, so its indexes are last session's pick order. It used
        // to be read back with Contains, which threw that order away.
        _pickCounter = config.QueueSteps.Count;

        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var savedIndex = config.QueueSteps.IndexOf(page.Kind);

            var step = new QueueStep(page)
            {
                CanonicalIndex = i,

                // Set before IsEnabled, and before the handler below is attached, so
                // restoring a session does not look like a fresh pick.
                PickedAt = savedIndex >= 0 ? savedIndex + 1 : 0,

                // First run has no saved list: default to everything off rather than
                // silently queueing a 40-minute cook the first time someone presses go.
                IsEnabled = savedIndex >= 0
            };

            step.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(QueueStep.IsEnabled))
                    return;

                step.PickedAt = step.IsEnabled ? ++_pickCounter : 0;

                Resort();
                PersistSteps();
                RaiseCommandStates();
            };

            Steps.Add(step);
        }

        Resort();

        RunQueueCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        StopCommand = new RelayCommand(Stop, () => _isRunning);

        _runner.RunningChanged += RaiseCommandStates;
    }

    public BuildContext Context { get; }

    public ObservableCollection<QueueStep> Steps { get; } = new();

    public ICommand RunQueueCommand { get; }
    public ICommand StopCommand { get; }

    public bool StopOnFailure
    {
        get => _config.QueueStopsOnFailure;
        set
        {
            if (_config.QueueStopsOnFailure == value)
                return;

            _config.QueueStopsOnFailure = value;

            OnPropertyChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
                RaiseCommandStates();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public int EnabledCount => Steps.Count(s => s.IsEnabled);

    public bool CanRun => !_isRunning && !_runner.IsRunning && EnabledCount > 0 && Context.HasProject;

    private async Task RunAsync()
    {
        var queued = Steps.Where(s => s.IsEnabled).ToList();

        if (queued.Count == 0)
            return;

        IsRunning = true;

        foreach (var step in Steps)
            step.State = step.IsEnabled ? QueueStepState.Waiting : QueueStepState.Disabled;

        _output.WriteTool($"Queue: {string.Join(" → ", queued.Select(s => s.Label))}");

        var stopped = false;

        try
        {
            foreach (var step in queued)
            {
                if (stopped)
                {
                    step.State = QueueStepState.Skipped;
                    continue;
                }

                // A step whose own validation is failing would silently do nothing and
                // report success, which is the worst possible outcome in a chain.
                if (!step.Page.CanRun)
                {
                    step.State = QueueStepState.Skipped;

                    _output.WriteTool(
                        $"Queue: skipping {step.Label} — {Reason(step.Page)}",
                        LogSeverity.Warning);

                    if (StopOnFailure)
                    {
                        stopped = true;
                        continue;
                    }

                    continue;
                }

                step.State = QueueStepState.Running;
                StatusText = $"Running {step.Label}";

                await step.Page.RunAsync();

                step.State = step.Page.LastOutcome switch
                {
                    BuildOutcome.Succeeded => QueueStepState.Succeeded,
                    BuildOutcome.Stopped => QueueStepState.Stopped,
                    _ => QueueStepState.Failed
                };

                if (step.Page.LastOutcome == BuildOutcome.Stopped)
                {
                    // Stop means stop the queue too, not just this step.
                    stopped = true;
                    continue;
                }

                if (step.Page.LastOutcome != BuildOutcome.Succeeded && StopOnFailure)
                {
                    stopped = true;

                    _output.WriteTool($"Queue: stopping after {step.Label} failed.", LogSeverity.Error);
                }
            }

            StatusText = Summarise(queued, stopped);
            _output.WriteTool($"Queue finished: {StatusText}");
        }
        finally
        {
            IsRunning = false;

            if (_resortPending)
                Resort();
        }
    }

    /// <summary>
    /// Puts the picked steps first, in the order they were picked, and the rest back in
    /// the order the shell listed them. The list on screen is then literally the run order,
    /// which is the only way a numbered queue is worth anything.
    /// </summary>
    private void Resort()
    {
        // Cards jumping around underneath a step that is currently building reads as a
        // bug. RunAsync has already snapshotted its list, so a pick made mid-run only
        // affects the next one anyway.
        if (_isRunning)
        {
            _resortPending = true;
            return;
        }

        _resortPending = false;

        var ordered = Steps
            .OrderBy(s => s.IsEnabled ? 0 : 1)
            .ThenBy(s => s.IsEnabled ? s.PickedAt : s.CanonicalIndex)
            .ToList();

        for (var target = 0; target < ordered.Count; target++)
        {
            var current = Steps.IndexOf(ordered[target]);

            // Move only when it actually moved: Move recreates the item container, and an
            // untouched list should not mutate at all.
            if (current != target)
                Steps.Move(current, target);
        }

        for (var i = 0; i < Steps.Count; i++)
            Steps[i].Position = Steps[i].IsEnabled ? i + 1 : 0;
    }

    private static string Reason(IBuildPage page)
        => page.HasValidationMessage ? page.ValidationMessage : "it is not ready to run";

    private static string Summarise(IReadOnlyList<QueueStep> queued, bool stopped)
    {
        var ok = queued.Count(s => s.State == QueueStepState.Succeeded);
        var failed = queued.Count(s => s.State == QueueStepState.Failed);
        var skipped = queued.Count(s => s.State is QueueStepState.Skipped or QueueStepState.Stopped);

        var parts = new List<string> { $"{ok} of {queued.Count} succeeded" };

        if (failed > 0)
            parts.Add($"{failed} failed");

        if (skipped > 0)
            parts.Add($"{skipped} not run");

        if (stopped)
            parts.Add("stopped early");

        return string.Join(" · ", parts);
    }

    private void Stop()
    {
        if (!_isRunning)
            return;

        StatusText = "Stopping";

        // Stops the step in flight; the loop sees Stopped and abandons the rest.
        foreach (var step in Steps.Where(s => s.Page.IsRunning))
            step.Page.StopCommand.Execute(null);

        _runner.Cancel();
    }

    /// <summary>
    /// Saves the picked steps in run order. <see cref="Resort"/> has already put
    /// <see cref="Steps"/> in that order, so the list written here is the one restored.
    /// </summary>
    private void PersistSteps()
    {
        _config.QueueSteps = Steps.Where(s => s.IsEnabled).Select(s => s.Page.Kind).ToList();

        OnPropertyChanged(nameof(EnabledCount));
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanRun));

        (RunQueueCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}

public enum QueueStepState
{
    Disabled,
    Waiting,
    Running,
    Succeeded,
    Failed,
    Stopped,
    Skipped
}

/// <summary>One entry in the queue: a build page, plus whether it is in and how it went.</summary>
public sealed class QueueStep : ObservableObject
{
    private bool _isEnabled;
    private int _position;
    private QueueStepState _state = QueueStepState.Disabled;

    public QueueStep(IBuildPage page)
    {
        Page = page;
    }

    public IBuildPage Page { get; }

    /// <summary>Where the shell listed this page. Keeps the unpicked steps in a stable order.</summary>
    public int CanonicalIndex { get; init; }

    /// <summary>
    /// When this step was picked, counting up for the life of the session. 0 while it is
    /// not picked, so unticking and reticking sends it to the back of the queue.
    /// </summary>
    public int PickedAt { get; set; }

    public string Label => Page.Kind switch
    {
        "package" => "Package",
        "nav" => "Navigation",
        "lighting" => "Lighting",
        _ => Page.Kind
    };

    public string Description => Page.Kind switch
    {
        "package" => "RunUAT BuildCookRun with the active preset",
        "nav" => "World Partition navigation data for the selected maps",
        "lighting" => "Static lighting for the selected maps",
        _ => ""
    };

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
                State = value ? QueueStepState.Waiting : QueueStepState.Disabled;
        }
    }

    /// <summary>1-based place in the run, or 0 when the step is not picked.</summary>
    public int Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value))
                OnPropertyChanged(nameof(PositionLabel));
        }
    }

    /// <summary>The badge on the card: "1", "2", "3", or nothing at all.</summary>
    public string PositionLabel => _position > 0 ? _position.ToString() : "";

    public QueueStepState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
                OnPropertyChanged(nameof(StateLabel));
        }
    }

    public string StateLabel => State switch
    {
        QueueStepState.Waiting => "Queued",
        QueueStepState.Running => "Running",
        QueueStepState.Succeeded => "Succeeded",
        QueueStepState.Failed => "Failed",
        QueueStepState.Stopped => "Stopped",
        QueueStepState.Skipped => "Not run",
        _ => ""
    };
}
