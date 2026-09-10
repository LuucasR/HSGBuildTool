using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// Compiles the project's C++ through UnrealBuildTool, and optionally follows it with the
/// two Blueprint steps that say whether the C++ that just compiled broke anything.
/// </summary>
/// <remarks>
/// Exists so a missing class fails in seconds rather than twenty minutes into a cook.
/// Being an <see cref="IBuildPage"/> it also queues, which is the point: Compile → Package
/// in one run.
///
/// Implements the interface directly rather than deriving from
/// <see cref="CommandletPageViewModel"/>, which loops over maps. This loops over steps
/// instead — up to three, each an opaque invocation — so it is the
/// <see cref="PackageViewModel"/> shape, minus the presets.
///
/// The three steps share one log session and produce one history record, because they are
/// one COMPILE as far as anyone pressing the button is concerned.
/// </remarks>
public sealed class CompilerViewModel : ObservableObject, IBuildPage
{
    private readonly BuildContext _context;
    private readonly ProcessRunner _runner;
    private readonly OutputService _output;
    private readonly AppConfig _config;
    private readonly BuildHistoryService _history;
    private readonly ElapsedTimer _elapsed = new();

    private ProjectSettings _settings = new();
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<CompileTarget> _discovered = Array.Empty<CompileTarget>();

    private string _commandPreview = "";
    private string _validationMessage = "";
    private string _statusText = "Ready";
    private string _estimateText = "";
    private string _blueprintSummary = "";
    private bool _isCompiling;

    public CompilerViewModel(
        BuildContext context,
        ProcessRunner runner,
        OutputService output,
        AppConfig config,
        BuildHistoryService history)
    {
        _context = context;
        _runner = runner;
        _output = output;
        _config = config;
        _history = history;

        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        StopCommand = new RelayCommand(Stop, () => _isCompiling);
        CopyCommandLineCommand = new RelayCommand(CopyCommandLine);
        SaveBatchFileCommand = new RelayCommand(SaveBatchFile, () => CommandPreview.Length > 0);
        RescanTargetsCommand = new RelayCommand(RescanTargets);

        RetryBlueprintsCommand = new AsyncRelayCommand(
            () => RunStepsAsync(includeCpp: false),
            () => CanRun && UpdateBlueprints);

        CopyBlueprintListCommand = new RelayCommand(CopyBlueprintList, () => BlueprintResults.Count > 0);

        OpenLogFileCommand = new RelayCommand(_output.OpenCurrentLogFile);
        OpenLogFolderCommand = new RelayCommand(_output.OpenLogFolder);

        LogExport = new LogExportViewModel(_output);

        _elapsed.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ElapsedText));

        _context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BuildContext.ProjectFile) or nameof(BuildContext.Engine))
                Refresh();
        };
    }

    // ---------------------------------------------------------------- options

    /// <summary>
    /// Target names, editor target first. Strings rather than the <see cref="CompileTarget"/>
    /// records themselves: the themed ComboBox renders its closed box through
    /// SelectionBoxItemTemplate, so a non-string item shows up as its type name.
    /// </summary>
    public ObservableCollection<string> Targets { get; } = new();

    /// <summary>"2 targets in Source" — says the scan happened, and what it found.</summary>
    public string TargetSummary => _discovered.Count switch
    {
        0 => "No C++ target found in Source. This project is Blueprint-only.",
        1 => "1 target in Source.",
        _ => $"{_discovered.Count} targets in Source."
    };

    /// <summary>Rescans Source, for a target added while the tool was open.</summary>
    public ICommand RescanTargetsCommand { get; }

    public IReadOnlyList<string> Configurations => CompileBuilder.Configurations;

    public string Target
    {
        get => _settings.CompileTarget;
        set
        {
            if (_settings.CompileTarget == value || value is null)
                return;

            _settings.CompileTarget = value;

            OnPropertyChanged();
            Refresh();
        }
    }

    public string Configuration
    {
        get => _settings.CompileConfiguration;
        set
        {
            if (_settings.CompileConfiguration == value || value is null)
                return;

            _settings.CompileConfiguration = value;

            OnPropertyChanged();
            Refresh();
        }
    }

    /// <summary>
    /// Run CompileAllBlueprints after the C++ build. A report, not a repair: the
    /// commandlet compiles in memory and saves nothing.
    /// </summary>
    public bool UpdateBlueprints
    {
        get => _settings.UpdateBlueprints;
        set
        {
            if (_settings.UpdateBlueprints == value)
                return;

            _settings.UpdateBlueprints = value;

            // The repair step works from this step's failures, and its checkbox is
            // disabled without it — so leaving it ticked would strand the page on a
            // validation error the user has no control left to clear.
            if (!value && _settings.UpdateAllNodesInBlueprints)
            {
                _settings.UpdateAllNodesInBlueprints = false;

                OnPropertyChanged(nameof(UpdateAllNodesInBlueprints));
            }

            OnPropertyChanged();
            Refresh();
        }
    }

    /// <summary>
    /// Reload, recompile and save the Blueprints the step above found broken. Not the
    /// editor's "Refresh All Nodes", which 5.7 does not expose to script — see
    /// <see cref="BlueprintScriptWriter"/>.
    /// </summary>
    public bool UpdateAllNodesInBlueprints
    {
        get => _settings.UpdateAllNodesInBlueprints;
        set
        {
            if (_settings.UpdateAllNodesInBlueprints == value)
                return;

            _settings.UpdateAllNodesInBlueprints = value;

            OnPropertyChanged();
            Refresh();
        }
    }

    /// <summary>Skip engine content, which this project could not fix even if it is broken.</summary>
    public bool BlueprintSkipEngineContent
    {
        get => _settings.BlueprintSkipEngineContent;
        set
        {
            if (_settings.BlueprintSkipEngineContent == value)
                return;

            _settings.BlueprintSkipEngineContent = value;

            OnPropertyChanged();
            Refresh();
        }
    }

    public string BlueprintExtraArguments
    {
        get => _settings.BlueprintExtraArguments;
        set
        {
            if (_settings.BlueprintExtraArguments == value || value is null)
                return;

            _settings.BlueprintExtraArguments = value;

            OnPropertyChanged();
            Refresh();
        }
    }

    private BlueprintOptions BlueprintOptions
        => new(UpdateBlueprints, UpdateAllNodesInBlueprints, BlueprintSkipEngineContent, BlueprintExtraArguments);

    /// <summary>The selected target as a record, for the validation that needs its type.</summary>
    private CompileTarget? SelectedTarget
        => _discovered.FirstOrDefault(t => t.Name.Equals(Target, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- blueprint results

    /// <summary>
    /// What the Blueprint steps found, for the results panel.
    /// </summary>
    /// <remarks>
    /// Filled from the awaiting thread once a step returns, never from the log handler:
    /// that fires on the process's stdout thread, and an ObservableCollection touched from
    /// there takes the UI down. Same reason the commandlet pages update their MapResults
    /// between passes rather than as output arrives.
    /// </remarks>
    public ObservableCollection<BlueprintResult> BlueprintResults { get; } = new();

    public bool HasBlueprintResults => BlueprintResults.Count > 0;

    /// <summary>"3 of 1,204 Blueprints failed" — the line above the list.</summary>
    public string BlueprintSummary
    {
        get => _blueprintSummary;
        private set => SetProperty(ref _blueprintSummary, value);
    }

    public int BlueprintFailedCount => BlueprintResults.Count(row =>
        row.State is not (BlueprintRunState.Reloaded or BlueprintRunState.Saved));

    /// <summary>Re-runs the Blueprint steps alone, without paying for the C++ compile again.</summary>
    public ICommand RetryBlueprintsCommand { get; }

    public ICommand CopyBlueprintListCommand { get; }

    // ---------------------------------------------------------------- contract

    public string Kind => "compile";

    public string RunButtonText => "COMPILE";

    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CopyCommandLineCommand { get; }
    public ICommand SaveBatchFileCommand { get; }
    public ICommand OpenLogFileCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public LogExportViewModel LogExport { get; }

    public bool IsRunning => IsCompiling;

    public bool CanRun => !_isCompiling && !_runner.IsRunning && ValidationMessage.Length == 0;

    /// <summary>True: none of the steps reports progress we can read.</summary>
    public bool IsProgressIndeterminate => true;

    public double Progress => 0;

    public BuildOutcome? LastOutcome { get; private set; }

    public bool IsCompiling
    {
        get => _isCompiling;
        private set
        {
            if (!SetProperty(ref _isCompiling, value))
                return;

            OnPropertyChanged(nameof(IsRunning));
            RaiseCommandStates();
        }
    }

    // ---------------------------------------------------------------- status

    public string CommandPreview
    {
        get => _commandPreview;
        private set => SetProperty(ref _commandPreview, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
                OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    public bool HasValidationMessage => !string.IsNullOrEmpty(ValidationMessage);

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ElapsedText => _elapsed.Text;

    public string EstimateText
    {
        get => _estimateText;
        private set => SetProperty(ref _estimateText, value);
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Rescans the project's targets and restores the saved selection.</summary>
    public Task OnProjectChangedAsync()
    {
        _settings = _context.HasProject ? _config.GetOrCreate(_context.ProjectFile) : new ProjectSettings();

        ScanTargets(announce: false);

        if (!CompileBuilder.Configurations.Contains(_settings.CompileConfiguration))
            _settings.CompileConfiguration = "Development";

        OnPropertyChanged(nameof(Configuration));
        OnPropertyChanged(nameof(UpdateBlueprints));
        OnPropertyChanged(nameof(UpdateAllNodesInBlueprints));
        OnPropertyChanged(nameof(BlueprintSkipEngineContent));
        OnPropertyChanged(nameof(BlueprintExtraArguments));

        ClearBlueprintResults();

        Refresh();

        return Task.CompletedTask;
    }

    private void RescanTargets()
    {
        ScanTargets(announce: true);

        Refresh();

        _output.WriteTool($"Compiler: {TargetSummary}");
    }

    /// <summary>
    /// Repopulates the target list and keeps the selection pointing at something real.
    /// </summary>
    private void ScanTargets(bool announce)
    {
        var saved = _settings.CompileTarget;

        _discovered = CompileBuilder.DiscoverTargets(_context.ProjectFile);

        Targets.Clear();

        foreach (var target in _discovered)
            Targets.Add(target.Name);

        // A target can vanish between sessions — a renamed module, a branch without it.
        // Falling back beats leaving the combo bound to a value no longer in its list.
        if (!Targets.Contains(saved, StringComparer.OrdinalIgnoreCase))
        {
            _settings.CompileTarget = CompileBuilder.DefaultTarget(_discovered);

            if (announce && !string.IsNullOrEmpty(saved) && _discovered.Count > 0)
            {
                _output.WriteTool(
                    $"The saved target \"{saved}\" is no longer in Source; using \"{_settings.CompileTarget}\".",
                    LogSeverity.Warning);
            }
        }

        OnPropertyChanged(nameof(Targets));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(Target));
    }

    // ---------------------------------------------------------------- run

    public Task RunAsync() => RunStepsAsync(includeCpp: true);

    /// <summary>
    /// Runs the steps in order, stopping at the first failure.
    /// </summary>
    /// <param name="includeCpp">
    /// False for "Retry blueprints", which re-runs the Blueprint steps against the
    /// binaries the last compile produced.
    /// </param>
    private async Task RunStepsAsync(bool includeCpp)
    {
        if (!_context.HasProject)
            return;

        if (_context.Engine is not { } engine)
        {
            _output.WriteTool(
                string.IsNullOrEmpty(_context.EngineError)
                    ? "No Unreal Engine installation resolved for this project."
                    : _context.EngineError,
                LogSeverity.Error);

            return;
        }

        if (_runner.IsRunning)
        {
            _output.WriteTool($"A build is already running ({_runner.CurrentDescription}). Stop it first.", LogSeverity.Warning);
            return;
        }

        if (includeCpp)
        {
            if (_discovered.Count == 0 || string.IsNullOrWhiteSpace(Target))
            {
                _output.WriteTool("There is no C++ target to compile in this project.", LogSeverity.Error);
                return;
            }

            if (!File.Exists(engine.BuildBat))
            {
                _output.WriteTool($"Build.bat was not found at {engine.BuildBat}.", LogSeverity.Error);
                return;
            }
        }
        else if (!UpdateBlueprints)
        {
            _output.WriteTool("Tick \"Update Blueprints\" before retrying the Blueprint steps.", LogSeverity.Warning);
            return;
        }

        IsCompiling = true;
        LastOutcome = null;

        _cancellation = new CancellationTokenSource();
        _elapsed.Restart();

        ClearBlueprintResults();

        _output.BeginSession("compile", _context.ProjectName, _context.ProjectFile);

        var result = new StepResult(BuildOutcome.Failed, -1, "");

        try
        {
            result = await RunSequenceAsync(engine, includeCpp);
        }
        catch (Exception ex)
        {
            result = new StepResult(BuildOutcome.Failed, -1, ex.Message);

            _output.WriteTool($"The run could not finish: {ex.Message}", LogSeverity.Error);
        }
        finally
        {
            _elapsed.Stop();

            _cancellation?.Dispose();
            _cancellation = null;

            LastOutcome = result.Outcome;
            StatusText = DescribeOutcome(result);

            _output.WriteTool(
                $"Compile finished after {_elapsed.Formatted}: {StatusText}",
                result.Outcome == BuildOutcome.Succeeded ? LogSeverity.Info : LogSeverity.Error);

            RecordHistory(result.ExitCode, includeCpp);

            _output.EndSession();

            IsCompiling = false;

            UpdateEstimate();
        }
    }

    /// <summary>
    /// The steps themselves. Each one's failure ends the run: updating Blueprints against
    /// binaries that failed to build reports failures that say nothing about Blueprints.
    /// </summary>
    private async Task<StepResult> RunSequenceAsync(EnginePaths engine, bool includeCpp)
    {
        var steps = (includeCpp ? 1 : 0) + (UpdateBlueprints ? 1 : 0) + (UpdateAllNodesInBlueprints ? 1 : 0);
        var step = 0;

        if (includeCpp)
        {
            StatusText = "Compiling C++";

            var cpp = await RunProcessStepAsync(
                ++step,
                steps,
                "Compile C++",
                engine.BuildBat,
                CompileBuilder.ToCommandLine(CompileBuilder.BuildArguments(_context.ProjectFile, Target, Configuration)),
                $"compile {Target} {Configuration}");

            if (cpp.Outcome != BuildOutcome.Succeeded)
            {
                if (UpdateBlueprints && cpp.Outcome == BuildOutcome.Failed)
                {
                    _output.WriteTool(
                        "Blueprints were not updated: the C++ compile did not succeed, so the editor would only load stale binaries.",
                        LogSeverity.Warning);
                }

                var reason = CompileBuilder.DescribeExitCode(cpp.ExitCode);

                return cpp with { Note = reason.Length > 0 ? reason : cpp.Note };
            }
        }

        if (!UpdateBlueprints)
            return new StepResult(BuildOutcome.Succeeded, 0, "");

        if (IsStopping)
            return new StepResult(BuildOutcome.Stopped, 0, "");

        // ---- Step: load and compile every Blueprint ----

        StatusText = "Updating Blueprints";

        var compileAll = await RunBlueprintStepAsync(
            ++step,
            steps,
            "Update Blueprints",
            engine,
            BlueprintBuilder.ToCommandLine(BlueprintBuilder.CompileAllArguments(_context.ProjectFile, BlueprintOptions)),
            "update blueprints",
            new BlueprintFailureCollector(BlueprintPassKind.CompileAll));

        if (compileAll.Outcome == BuildOutcome.Stopped)
            return compileAll;

        var reportA = compileAll.Report;

        if (reportA is null)
            return compileAll;

        Publish(reportA.Results);

        ReportBlueprintPass(reportA, "compiled");

        if (!reportA.Conclusive)
            return new StepResult(BuildOutcome.Failed, compileAll.ExitCode, "Blueprint results could not be read");

        var broken = reportA.UnresolvedPaths;

        // The commandlet counted errors and the page could not say whose. Going green on
        // that is the same lie as showing an empty failure list — the log has the detail,
        // and the run has to send someone to it rather than wave them past.
        if (reportA.Totals is { Errors: > 0 } counted && broken.Count == 0)
        {
            BlueprintSummary = $"{counted.Errors} Blueprint error(s) reported, but none could be tied to an asset.";

            return new StepResult(
                BuildOutcome.Failed,
                compileAll.ExitCode,
                $"{counted.Errors} Blueprint error(s) could not be attributed");
        }

        if (broken.Count == 0)
        {
            BlueprintSummary = $"{reportA.Attempted:N0} Blueprints compiled, none failed.";

            if (UpdateAllNodesInBlueprints)
                _output.WriteTool("Nothing to repair: every Blueprint compiled, so the last step was skipped.");

            return new StepResult(BuildOutcome.Succeeded, 0, $"{reportA.Attempted:N0} Blueprints");
        }

        BlueprintSummary = $"{broken.Count} of {reportA.Attempted:N0} Blueprints failed to compile.";

        if (!UpdateAllNodesInBlueprints)
            return new StepResult(BuildOutcome.Failed, compileAll.ExitCode, $"{broken.Count} Blueprint(s) failed");

        if (IsStopping)
            return new StepResult(BuildOutcome.Stopped, 0, "");

        // ---- Step: reload the ones that failed, compile them again and save them ----

        StatusText = $"Reloading {broken.Count} Blueprint(s)";

        var scriptPath = WriteRefreshScript(broken);

        if (scriptPath.Length == 0)
            return new StepResult(BuildOutcome.Failed, -1, "the repair script could not be written");

        var refresh = await RunBlueprintStepAsync(
            ++step,
            steps,
            "Reload and resave",
            engine,
            BlueprintBuilder.ToCommandLine(BlueprintBuilder.RefreshArguments(_context.ProjectFile, scriptPath, BlueprintOptions)),
            "reload broken blueprints",
            new BlueprintFailureCollector(BlueprintPassKind.Refresh));

        if (refresh.Outcome == BuildOutcome.Stopped)
            return refresh;

        var reportB = refresh.Report;

        if (reportB is null)
            return refresh;

        ReportBlueprintPass(reportB, "reloaded");

        if (!reportB.Conclusive)
        {
            // Deliberately leaves the previous step's failures on screen. Replacing a real
            // list of broken Blueprints with one "(unknown)" row would throw away the only
            // thing this run did establish.
            return new StepResult(BuildOutcome.Failed, refresh.ExitCode, "the refresh step could not be read");
        }

        Publish(reportB.Results);

        var fixedUp = reportB.Results.Count(row => row.State is BlueprintRunState.Reloaded or BlueprintRunState.Saved);
        var remaining = reportB.Unresolved.Count;

        BlueprintSummary = remaining == 0
            ? $"{fixedUp} Blueprint(s) reloaded and saved."
            : $"{fixedUp} reloaded, {remaining} still failing.";

        return remaining == 0
            ? new StepResult(BuildOutcome.Succeeded, 0, $"{fixedUp} Blueprint(s) reloaded")
            : new StepResult(BuildOutcome.Failed, refresh.ExitCode, $"{remaining} Blueprint(s) still failing");
    }

    /// <summary>
    /// Runs one external command as a step of this run.
    /// </summary>
    /// <remarks>
    /// The try/catch is not defensive padding. <see cref="ProcessRunner.RunAsync"/> throws
    /// when it is already busy, and <see cref="IsCompiling"/> does not hold the runner's
    /// lock — so every gap between two steps is a window for another page to take it. That
    /// has to fail this step, not the whole method with an unhandled exception.
    /// </remarks>
    private async Task<StepResult> RunProcessStepAsync(
        int step,
        int steps,
        string label,
        string exe,
        string commandLine,
        string description)
    {
        _output.WriteTool($"──── Step {step} of {steps} · {label} ────");
        _output.WriteTool($"{exe} {commandLine}");

        try
        {
            var exitCode = await _runner.RunAsync(exe, commandLine, _context.ProjectDirectory, description, _cancellation!.Token);

            if (IsStopping)
                return new StepResult(BuildOutcome.Stopped, exitCode, "");

            return exitCode == 0
                ? new StepResult(BuildOutcome.Succeeded, 0, "")
                : new StepResult(BuildOutcome.Failed, exitCode, "");
        }
        catch (Exception ex)
        {
            _output.WriteTool($"{label} could not start: {ex.Message}", LogSeverity.Error);

            return new StepResult(BuildOutcome.Failed, -1, ex.Message);
        }
    }

    /// <summary>
    /// A step whose log is read as it arrives, so the run can name the Blueprints that
    /// failed rather than leaving a number and a 200,000-line log.
    /// </summary>
    private async Task<StepResult> RunBlueprintStepAsync(
        int step,
        int steps,
        string label,
        EnginePaths engine,
        string commandLine,
        string description,
        BlueprintFailureCollector collector)
    {
        void Observe(LogEntry entry) => collector.Observe(entry);

        _output.EntryAdded += Observe;

        StepResult result;

        try
        {
            result = await RunProcessStepAsync(step, steps, label, engine.EditorCmd, commandLine, description);
        }
        finally
        {
            // Unsubscribing does not cancel an invocation already in flight; the collector
            // ignores whatever arrives after Complete().
            _output.EntryAdded -= Observe;
        }

        return result with { Report = collector.Complete(result.ExitCode) };
    }

    // ---------------------------------------------------------------- blueprint plumbing

    /// <summary>
    /// Writes the repair script beside this run's log, so a post-mortem has the exact
    /// script and the exact log together. Empty on failure, which fails the step.
    /// </summary>
    private string WriteRefreshScript(IReadOnlyList<string> paths)
    {
        try
        {
            var directory = Path.GetDirectoryName(_output.CurrentLogFile);

            if (string.IsNullOrEmpty(directory))
                directory = _output.LogDirectory;

            Directory.CreateDirectory(directory);

            var name = Path.GetFileNameWithoutExtension(_output.CurrentLogFile);

            if (string.IsNullOrEmpty(name))
                name = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            var path = Path.Combine(directory, $"{name}-refresh.py");

            File.WriteAllText(path, BlueprintScriptWriter.ForPaths(paths, save: true));

            _output.WriteTool($"Wrote the repair script to {path}.");

            return path;
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not write the repair script: {ex.Message}", LogSeverity.Error);

            return "";
        }
    }

    /// <summary>Replaces the results list. Called after a step's await, on the UI thread.</summary>
    private void Publish(IReadOnlyList<BlueprintResult> rows)
    {
        BlueprintResults.Clear();

        foreach (var row in rows)
            BlueprintResults.Add(row);

        OnPropertyChanged(nameof(HasBlueprintResults));
        OnPropertyChanged(nameof(BlueprintFailedCount));

        (CopyBlueprintListCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void ClearBlueprintResults()
    {
        BlueprintResults.Clear();
        BlueprintSummary = "";

        OnPropertyChanged(nameof(HasBlueprintResults));
        OnPropertyChanged(nameof(BlueprintFailedCount));
    }

    /// <summary>Writes what a Blueprint step found into the log, one line per Blueprint.</summary>
    private void ReportBlueprintPass(BlueprintPassReport report, string verb)
    {
        if (report.Diagnosis.Length > 0)
            _output.WriteTool(report.Diagnosis, LogSeverity.Warning);

        if (!report.Conclusive)
            return;

        foreach (var row in report.Results)
        {
            var severity = row.State is BlueprintRunState.Reloaded or BlueprintRunState.Saved
                ? LogSeverity.Info
                : LogSeverity.Error;

            var detail = row.FirstError.Length > 0 ? $" — {row.FirstError}" : "";

            _output.WriteTool($"  {row.StateLabel}: {row.Path}{detail}", severity);
        }

        if (report.ForeignFailureCount > 0)
        {
            // Counted, not listed: nobody can fix an engine or plugin Blueprint from here,
            // and a page full of them would bury the ones they can.
            _output.WriteTool(
                $"{report.ForeignFailureCount} Blueprint(s) outside /Game also failed. They are engine or plugin content and are not listed.",
                LogSeverity.Warning);
        }

        if (report.StartupErrorCount > 0)
        {
            _output.WriteTool(
                $"{report.StartupErrorCount} error(s) were logged before the first Blueprint and belong to the editor's start-up, not to any asset.",
                LogSeverity.Warning);
        }

        // The commandlet's own tally, printed alongside ours rather than instead of it: if
        // the two disagree, that is the thing worth seeing in the log.
        var tally = report.Totals is { } totals
            ? $" The commandlet counted {totals.Errors} error(s) and {totals.Warnings} warning(s)."
            : "";

        _output.WriteTool($"{report.Attempted:N0} Blueprints {verb}.{tally}");
    }

    // ---------------------------------------------------------------- outcome

    private bool IsStopping => _cancellation?.IsCancellationRequested ?? false;

    private string DescribeOutcome(StepResult result)
    {
        var note = result.Note.Length > 0 ? $" — {result.Note}" : "";

        return result.Outcome switch
        {
            BuildOutcome.Succeeded => $"Succeeded in {_elapsed.Formatted}{note}",
            BuildOutcome.Stopped => $"Stopped after {_elapsed.Formatted}",
            _ when result.ExitCode > 0 => $"Failed ({result.ExitCode}) after {_elapsed.Formatted}{note}",
            _ => $"Failed after {_elapsed.Formatted}{note}"
        };
    }

    private void RecordHistory(int exitCode, bool includeCpp)
    {
        if (LastOutcome is not { } outcome)
            return;

        var detail = includeCpp ? $"{Target} {Configuration}" : "blueprints only";

        if (includeCpp && UpdateBlueprints)
            detail += UpdateAllNodesInBlueprints ? " · blueprints+refresh" : " · blueprints";

        _history.Record(new BuildRecord
        {
            Kind = "compile",
            ProjectFile = _context.ProjectFile,
            ProjectName = _context.ProjectName,
            Detail = detail,
            StartedAt = _output.SessionStartedAt ?? DateTime.Now,
            DurationSeconds = _elapsed.Elapsed.TotalSeconds,
            Outcome = outcome,
            ExitCode = exitCode,
            LogFile = _output.CurrentLogFile ?? "",
            Warnings = _output.SessionWarnings,
            Errors = _output.SessionErrors,
            GitBranch = _output.SessionGit.Branch,
            GitCommit = _output.SessionGit.Commit
        });
    }

    /// <summary>Cancels the run and kills the step that is currently running.</summary>
    private void Stop()
    {
        if (!_isCompiling)
            return;

        StatusText = "Stopping";

        _cancellation?.Cancel();
        _runner.Cancel();
    }

    // ---------------------------------------------------------------- actions

    private void CopyCommandLine() => CopyToClipboard(CommandPreview, "Command copied to the clipboard.");

    private void CopyBlueprintList()
    {
        if (BlueprintResults.Count == 0)
            return;

        var text = string.Join(
            Environment.NewLine,
            BlueprintResults.Select(row => $"{row.StateLabel}\t{row.Path}\t{row.FirstError}"));

        CopyToClipboard(text, $"{BlueprintResults.Count} Blueprint(s) copied to the clipboard.");
    }

    private void CopyToClipboard(string text, string confirmation)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            Clipboard.SetText(text);
            _output.WriteTool(confirmation);
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not copy to the clipboard: {ex.Message}", LogSeverity.Warning);
        }
    }

    /// <summary>
    /// Writes the run as a .bat, plus the Python it would need.
    /// </summary>
    /// <remarks>
    /// The exported refresh step cannot be the one the UI runs: a .bat has no way to read
    /// the previous step's failure list, so the script it gets refreshes and saves every
    /// Blueprint under /Game. That is a wider job than the UI does, and the .bat says so
    /// rather than quietly doing less than it looks like.
    /// </remarks>
    private void SaveBatchFile()
    {
        if (_context.Engine is not { } engine || string.IsNullOrWhiteSpace(CommandPreview))
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Batch file (*.bat)|*.bat",
            FileName = $"compile-{_context.ProjectName}-{Configuration}.bat"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var steps = new List<(string Exe, string Arguments, string Label)>
            {
                (engine.BuildBat,
                    CompileBuilder.ToCommandLine(CompileBuilder.BuildArguments(_context.ProjectFile, Target, Configuration)),
                    $"Compile {Target} {Configuration}")
            };

            var notes = new List<string>();
            var scriptFile = "";

            if (UpdateBlueprints)
            {
                steps.Add((engine.EditorCmd,
                    BlueprintBuilder.ToCommandLine(BlueprintBuilder.CompileAllArguments(_context.ProjectFile, BlueprintOptions)),
                    "Update Blueprints"));
            }

            if (UpdateBlueprints && UpdateAllNodesInBlueprints)
            {
                scriptFile = Path.GetFileNameWithoutExtension(dialog.FileName) + "-refresh.py";

                notes.Add("The refresh step below refreshes and SAVES every Blueprint under /Game,");
                notes.Add("not only the ones that failed above: a .bat cannot read the previous");
                notes.Add("step's failure list. The tool's own run only touches the failures.");

                steps.Add((engine.EditorCmd,
                    BlueprintBuilder.ToCommandLine(
                        BlueprintBuilder.RefreshArguments(_context.ProjectFile, $"%~dp0{scriptFile}", BlueprintOptions)),
                    "Reload and resave Blueprints"));
            }

            File.WriteAllText(
                dialog.FileName,
                BatchScriptWriter.ForSteps($"Compile {_context.ProjectName}", _context.ProjectDirectory, steps, notes));

            _output.WriteTool($"Saved {dialog.FileName}.");

            if (scriptFile.Length > 0)
            {
                var scriptPath = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, scriptFile);

                File.WriteAllText(scriptPath, BlueprintScriptWriter.ForAllBlueprints(save: true));

                _output.WriteTool($"Saved {scriptPath}.");
            }
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not save the batch file: {ex.Message}", LogSeverity.Error);
        }
    }

    // ---------------------------------------------------------------- plumbing

    private void Refresh()
    {
        var problems = new List<string>(
            CompileBuilder.Validate(_context.ProjectFile, _discovered, Target, Configuration, _context.Engine));

        problems.AddRange(
            BlueprintBuilder.Validate(_context.ProjectFile, _context.Engine, SelectedTarget, BlueprintOptions));

        if (_context.HasProject && _context.Engine is null)
        {
            problems.Add(string.IsNullOrEmpty(_context.EngineError)
                ? "No Unreal Engine installation resolved."
                : _context.EngineError);
        }

        ValidationMessage = string.Join("  ·  ", problems);

        CommandPreview = BuildPreview();

        UpdateEstimate();

        OnPropertyChanged(nameof(CanRun));

        RaiseCommandStates();
    }

    /// <summary>
    /// One line per step, as the run would issue them. The refresh step is shown as a REM
    /// because its script does not exist until the step before it has found something.
    /// </summary>
    private string BuildPreview()
    {
        if (_context is not { HasProject: true, Engine: { } engine } || string.IsNullOrWhiteSpace(Target))
            return "";

        var lines = new List<string>
        {
            $"\"{engine.BuildBat}\" {CompileBuilder.ToCommandLine(CompileBuilder.BuildArguments(_context.ProjectFile, Target, Configuration))}"
        };

        if (UpdateBlueprints)
        {
            lines.Add(
                $"\"{engine.EditorCmd}\" {BlueprintBuilder.ToCommandLine(BlueprintBuilder.CompileAllArguments(_context.ProjectFile, BlueprintOptions))}");
        }

        if (UpdateBlueprints && UpdateAllNodesInBlueprints)
        {
            lines.Add("REM then, for each Blueprint that failed above:");
            lines.Add(
                $"REM \"{engine.EditorCmd}\" {BlueprintBuilder.ToCommandLine(BlueprintBuilder.RefreshArguments(_context.ProjectFile, "<generated when the run starts>", BlueprintOptions))}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void UpdateEstimate()
    {
        var estimate = _context.HasProject ? _history.Estimate("compile", _context.ProjectFile) : null;

        EstimateText = estimate is { } value ? $"~{value:hh\\:mm\\:ss} last time" : "";
    }

    private void RaiseCommandStates()
    {
        (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RetryBlueprintsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveBatchFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyBlueprintListCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>How one step ended, and what the Blueprint steps read out of its log.</summary>
    private sealed record StepResult(BuildOutcome Outcome, int ExitCode, string Note)
    {
        public BlueprintPassReport? Report { get; init; }
    }
}
