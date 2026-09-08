using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.Views;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// Shared behaviour for the pages that drive UnrealEditor-Cmd commandlets over a set
/// of maps (Navigation, Lighting, HLOD): named map presets, validation, command preview,
/// and a sequential per-map run with progress and per-map results.
/// </summary>
/// <remarks>
/// The per-map loop is the important part. The old navigation code passed every
/// selected map to one invocation as space-separated positional arguments, which the
/// commandlet cannot consume — only the first map was ever built, and the rest were
/// reported as successful.
///
/// Each map's turn is a list of <see cref="CommandletPass"/>es rather than one command.
/// Navigation and Lighting have exactly one, so nothing changes for them; HLOD deletes
/// and then builds, which has to be two runs of the commandlet. A map stops at its first
/// failing pass — there is no point building HLODs whose delete pass just failed — and
/// counts as one failure, so the results panel still reads one row per map.
/// </remarks>
public abstract class CommandletPageViewModel : ObservableObject, IBuildPage
{
    private readonly ElapsedTimer _elapsed = new();

    private CancellationTokenSource? _cancellation;
    private CommandletPreset _preset = new();
    private CommandletPreset? _selectedPreset;
    private string _commandPreview = "";
    private string _validationMessage = "";
    private string _statusText = "Ready";
    private string _estimateText = "";
    private double _progress;
    private bool _isRunning;
    private bool _suspendWrite;

    protected CommandletPageViewModel(
        BuildContext context,
        ProcessRunner runner,
        OutputService output,
        AppConfig config,
        BuildHistoryService history)
    {
        Context = context;
        Runner = runner;
        Output = output;
        Config = config;
        History = history;

        MapSelection = new MapSelectionViewModel();
        MapSelection.SelectionChanged += OnMapSelectionChanged;

        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        StopCommand = new RelayCommand(Stop, () => _isRunning);
        CopyCommandLineCommand = new RelayCommand(CopyCommandLine);
        SaveBatchFileCommand = new RelayCommand(SaveBatchFile, () => CommandPreview.Length > 0);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync, () => FailedCount > 0 && !_isRunning);

        SavePresetCommand = new RelayCommand(SavePreset);
        SaveAsPresetCommand = new RelayCommand(SaveAsPreset);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => Presets.Count > 1);

        // The log lives in the service, so the page can offer it without going through
        // the shell's log view-model.
        OpenLogFileCommand = new RelayCommand(Output.OpenCurrentLogFile);
        OpenLogFolderCommand = new RelayCommand(Output.OpenLogFolder);

        LogExport = new LogExportViewModel(Output);

        _elapsed.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ElapsedText));

        Context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BuildContext.ProjectFile) or nameof(BuildContext.Engine))
                Refresh();
        };
    }

    protected BuildContext Context { get; }
    protected ProcessRunner Runner { get; }
    protected OutputService Output { get; }
    protected AppConfig Config { get; }
    protected BuildHistoryService History { get; }

    protected ProjectSettings Settings { get; private set; } = new();

    /// <summary>
    /// The preset being edited. Page-specific options write into it as they change, the
    /// way Package's do, so switching preset and switching back does not silently keep
    /// the other preset's settings.
    /// </summary>
    protected CommandletPreset ActivePreset => _preset;

    public MapSelectionViewModel MapSelection { get; }

    /// <summary>Per-map outcome of the last run. Empty until something has been built.</summary>
    public ObservableCollection<MapResult> Results { get; } = new();

    public ObservableCollection<CommandletPreset> Presets { get; } = new();

    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CopyCommandLineCommand { get; }
    public ICommand SaveBatchFileCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public ICommand OpenLogFileCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public LogExportViewModel LogExport { get; }
    public ICommand SavePresetCommand { get; }
    public ICommand SaveAsPresetCommand { get; }
    public ICommand DeletePresetCommand { get; }

    /// <summary>"nav", "lighting" or "hlod" — names the log file, the history entry and the queue step.</summary>
    public abstract string Kind { get; }

    /// <summary>Verb shown on the primary button, e.g. "BUILD NAVIGATION".</summary>
    public abstract string RunButtonText { get; }

    /// <summary>Live "hh:mm:ss", and the final duration once the run ends.</summary>
    public string ElapsedText => _elapsed.Text;

    /// <summary>False: these pages count maps, so the bar shows real progress.</summary>
    public bool IsProgressIndeterminate => false;

    public BuildOutcome? LastOutcome { get; private set; }

    /// <summary>Human-readable name of the operation, used in log lines.</summary>
    protected abstract string ActionName { get; }

    /// <summary>The page's single invocation for a map. Pages with more than one override
    /// <see cref="PassesFor"/> instead, and this returns their main one.</summary>
    protected abstract IReadOnlyList<string> ArgumentsFor(string map);

    /// <summary>
    /// Everything that has to run for one map, in order. One unnamed pass by default:
    /// naming the step would only repeat the page on a page that has a single one.
    /// </summary>
    protected virtual IReadOnlyList<CommandletPass> PassesFor(string map)
        => new[] { new CommandletPass("", ArgumentsFor(map)) };

    protected abstract IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps);

    /// <summary>Pull page-specific state out of a preset that has just been selected.</summary>
    protected virtual void OnPresetApplied(CommandletPreset preset)
    {
    }

    /// <summary>Push page-specific state into the preset before it is saved or run.</summary>
    protected virtual void CaptureIntoPreset(CommandletPreset preset)
    {
    }

    /// <summary>Extra detail recorded in build history, e.g. the lighting quality.</summary>
    protected virtual string HistoryDetail => "";

    public CommandletPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || value is null)
                return;

            _preset = value;

            Settings.SetActiveCommandletPreset(Kind, value.Name);

            ApplyPresetToUi();
        }
    }

    public string CommandPreview
    {
        get => _commandPreview;
        private set
        {
            if (SetProperty(ref _commandPreview, value))
                (SaveBatchFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
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
        protected set => SetProperty(ref _statusText, value);
    }

    /// <summary>"~12:34 last time" — what history says this run costs.</summary>
    public string EstimateText
    {
        get => _estimateText;
        private set => SetProperty(ref _estimateText, value);
    }

    /// <summary>0-100 across the selected maps, so a long multi-map run shows real progress.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
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

    public bool CanRun => !_isRunning && !Runner.IsRunning && ValidationMessage.Length == 0;

    public bool HasResults => Results.Count > 0;

    public int FailedCount => Results.Count(r => r.State == MapRunState.Failed);

    public string ResultsSummary
    {
        get
        {
            if (Results.Count == 0)
                return "";

            var ok = Results.Count(r => r.State == MapRunState.Succeeded);
            var failed = FailedCount;
            var skipped = Results.Count(r => r.State == MapRunState.Skipped);

            var parts = new List<string> { $"{ok} succeeded" };

            if (failed > 0)
                parts.Add($"{failed} failed");

            if (skipped > 0)
                parts.Add($"{skipped} not run");

            return string.Join(" · ", parts);
        }
    }

    public async Task OnProjectChangedAsync()
    {
        if (!Context.HasProject)
        {
            Presets.Clear();
            Refresh();

            return;
        }

        // Suspended while loading: scanning raises SelectionChanged, and writing the
        // still-empty selection back into the preset would erase the saved one.
        _suspendWrite = true;

        try
        {
            Settings = Config.GetOrCreate(Context.ProjectFile);

            Presets.Clear();

            foreach (var preset in Settings.PresetsFor(Kind))
                Presets.Add(preset);

            _selectedPreset = Settings.GetActiveCommandletPreset(Kind);
            _preset = _selectedPreset;

            OnPropertyChanged(nameof(SelectedPreset));

            OnPresetApplied(_preset);

            await MapSelection.LoadAsync(Context.ProjectFile, _preset.Maps);
        }
        finally
        {
            _suspendWrite = false;
        }

        ClearResults();
        Refresh();
    }

    private void ApplyPresetToUi()
    {
        _suspendWrite = true;

        try
        {
            OnPresetApplied(_preset);

            MapSelection.ApplySelection(_preset.Maps);
        }
        finally
        {
            _suspendWrite = false;
        }

        Refresh();
    }

    private void OnMapSelectionChanged()
    {
        if (_suspendWrite)
            return;

        if (Context.HasProject)
            _preset.Maps = MapSelection.SelectedMaps.ToList();

        Refresh();
    }

    // ---------------------------------------------------------------- run

    public async Task RunAsync()
    {
        var maps = MapSelection.SelectedMaps;

        if (Context.Engine is not { } engine)
        {
            Output.WriteTool(
                string.IsNullOrEmpty(Context.EngineError)
                    ? "No Unreal Engine installation resolved for this project."
                    : Context.EngineError,
                LogSeverity.Error);

            return;
        }

        if (Runner.IsRunning)
        {
            Output.WriteTool($"A build is already running ({Runner.CurrentDescription}). Stop it first.", LogSeverity.Warning);
            return;
        }

        CaptureIntoPreset(_preset);

        IsRunning = true;
        LastOutcome = null;

        _cancellation = new CancellationTokenSource();
        _elapsed.Restart();

        // The whole plan up front, so the results panel shows what is still to come
        // rather than growing a line at a time.
        ResetResults(maps);

        Output.BeginSession(Kind, Context.ProjectName, Context.ProjectFile);
        Output.WriteTool($"Starting {ActionName} for {maps.Count} map(s).");

        var failed = new List<string>();
        var stopped = false;

        try
        {
            for (var i = 0; i < maps.Count; i++)
            {
                if (_cancellation.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                var result = Results[i];

                result.State = MapRunState.Running;

                var state = await RunMapAsync(engine, maps, i, result);

                if (state == MapRunState.Failed)
                    failed.Add(maps[i]);

                if (state == MapRunState.Skipped)
                {
                    stopped = true;

                    break;
                }
            }

            Progress = 100;

            LastOutcome = stopped
                ? BuildOutcome.Stopped
                : failed.Count == 0 ? BuildOutcome.Succeeded : BuildOutcome.Failed;

            WriteSummary(maps, failed, stopped);
        }
        finally
        {
            // Stop, not Reset: the run's total is the number worth leaving on screen.
            _elapsed.Stop();

            _cancellation?.Dispose();
            _cancellation = null;

            RecordHistory(maps.Count);

            Output.EndSession();

            IsRunning = false;
            Progress = 0;

            RaiseResultState();
            UpdateEstimate();
        }
    }

    /// <summary>
    /// Runs every pass of one map in order, stopping at the first that fails or is
    /// cancelled, and stamps the result. Returns what became of the map as a whole.
    /// </summary>
    /// <remarks>
    /// The map is one row in the results panel however many passes it took, so a failed
    /// delete pass fails the map rather than letting the build pass run against HLODs
    /// that are still there.
    /// </remarks>
    private async Task<MapRunState> RunMapAsync(
        EnginePaths engine,
        IReadOnlyList<string> maps,
        int index,
        MapResult result)
    {
        var map = maps[index];
        var passes = PassesFor(map);
        var watch = Stopwatch.StartNew();

        for (var pass = 0; pass < passes.Count; pass++)
        {
            if (_cancellation!.IsCancellationRequested)
            {
                Finish(result, watch, MapRunState.Skipped, 0);

                return MapRunState.Skipped;
            }

            var step = passes[pass].Label.Length > 0 ? $" — {passes[pass].Label}" : "";

            StatusText = $"{ActionName}: {index + 1} of {maps.Count} — {map}{step}";

            // Each pass advances the bar by a fraction of the map's share, so a two-pass
            // HLOD build does not sit on the same number for twice as long.
            Progress = (index + (double)pass / passes.Count) * 100.0 / maps.Count;

            var commandLine = string.Join(" ", passes[pass].Arguments);

            Output.WriteTool($"[{index + 1}/{maps.Count}]{step} {engine.EditorCmd} {commandLine}");

            int exitCode;

            try
            {
                exitCode = await Runner.RunAsync(
                    engine.EditorCmd,
                    commandLine,
                    Context.ProjectDirectory,
                    $"{ActionName} ({map}{step})",
                    _cancellation.Token);
            }
            catch (Exception ex)
            {
                Output.WriteTool($"{map}{step}: could not start — {ex.Message}", LogSeverity.Error);

                Finish(result, watch, MapRunState.Failed, -1);

                return MapRunState.Failed;
            }

            if (_cancellation.IsCancellationRequested)
            {
                Finish(result, watch, MapRunState.Skipped, exitCode);

                return MapRunState.Skipped;
            }

            if (exitCode != 0)
            {
                Output.WriteTool($"{map}{step}: FAILED (exit code {exitCode})", LogSeverity.Error);

                Finish(result, watch, MapRunState.Failed, exitCode);

                return MapRunState.Failed;
            }
        }

        Finish(result, watch, MapRunState.Succeeded, 0);
        Output.WriteTool($"{map}: OK");

        return MapRunState.Succeeded;
    }

    /// <summary>
    /// Re-runs only the maps that failed or never started.
    /// </summary>
    /// <remarks>
    /// The narrowed selection is a temporary state of the run, not an edit of the preset:
    /// suspending the write and putting the original selection back afterwards is what
    /// stops one retry from permanently reducing "all sixteen maps" to "the two that
    /// broke". The preset is what the next full build reads.
    /// </remarks>
    private async Task RetryFailedAsync()
    {
        var failed = Results
            .Where(r => r.State is MapRunState.Failed or MapRunState.Skipped)
            .Select(r => r.Map)
            .ToList();

        if (failed.Count == 0)
            return;

        var saved = _preset.Maps.ToList();

        Select(failed);

        try
        {
            await RunAsync();
        }
        finally
        {
            Select(saved);
            Refresh();
        }
    }

    /// <summary>Changes what is ticked without treating it as an edit of the preset.</summary>
    private void Select(IReadOnlyList<string> maps)
    {
        _suspendWrite = true;

        try
        {
            MapSelection.ApplySelection(maps);
        }
        finally
        {
            _suspendWrite = false;
        }
    }

    private static void Finish(MapResult result, Stopwatch watch, MapRunState state, int exitCode)
    {
        watch.Stop();

        result.DurationSeconds = watch.Elapsed.TotalSeconds;
        result.ExitCode = exitCode;

        // A skipped map never ran, so stamping it with a time would claim work that did
        // not happen.
        result.FinishedAt = state == MapRunState.Skipped ? null : DateTime.Now;

        result.State = state;
    }

    private void ResetResults(IReadOnlyList<string> maps)
    {
        Results.Clear();

        foreach (var map in maps)
            Results.Add(new MapResult { Map = map });

        RaiseResultState();
    }

    private void ClearResults()
    {
        Results.Clear();
        RaiseResultState();
    }

    private void RaiseResultState()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(ResultsSummary));

        (RetryFailedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RecordHistory(int mapCount)
    {
        if (LastOutcome is not { } outcome)
            return;

        History.Record(new BuildRecord
        {
            Kind = Kind,
            ProjectFile = Context.ProjectFile,
            ProjectName = Context.ProjectName,
            Detail = string.IsNullOrEmpty(HistoryDetail)
                ? $"{mapCount} map(s)"
                : $"{HistoryDetail} · {mapCount} map(s)",
            StartedAt = Output.SessionStartedAt ?? DateTime.Now,
            DurationSeconds = _elapsed.Elapsed.TotalSeconds,
            Outcome = outcome,
            ExitCode = outcome == BuildOutcome.Succeeded ? 0 : 1,
            LogFile = Output.CurrentLogFile ?? "",
            Warnings = Output.SessionWarnings,
            Errors = Output.SessionErrors,
            GitBranch = Output.SessionGit.Branch,
            GitCommit = Output.SessionGit.Commit
        });
    }

    private void WriteSummary(IReadOnlyList<string> maps, IReadOnlyList<string> failed, bool stopped)
    {
        var elapsed = _elapsed.Formatted;
        var succeeded = maps.Count - failed.Count;

        if (stopped)
        {
            // "Stopped", not "cancelled": the button says Stop, and the status line is the
            // first place you look to confirm the button did what it said.
            StatusText = $"Stopped after {elapsed}";
            Output.WriteTool($"{ActionName} stopped after {elapsed}. {succeeded} map(s) completed.", LogSeverity.Warning);

            return;
        }

        if (failed.Count == 0)
        {
            StatusText = $"{maps.Count} map(s) in {elapsed}";
            Output.WriteTool($"{ActionName} finished: all {maps.Count} map(s) succeeded in {elapsed}.");

            return;
        }

        StatusText = $"{failed.Count} of {maps.Count} failed";

        Output.WriteTool(
            $"{ActionName} finished in {elapsed}: {succeeded} succeeded, {failed.Count} failed — {string.Join(", ", failed)}",
            LogSeverity.Error);
    }

    /// <summary>
    /// Stops the whole run: cancels the loop so no further map is started, and kills the
    /// process tree of the map currently in flight.
    /// </summary>
    private void Stop()
    {
        if (!_isRunning)
            return;

        StatusText = "Stopping";

        _cancellation?.Cancel();
        Runner.Cancel();
    }

    // ---------------------------------------------------------------- presets

    private void SavePreset()
    {
        CaptureIntoPreset(_preset);

        _preset.Maps = MapSelection.SelectedMaps.ToList();

        Settings.SetActiveCommandletPreset(Kind, _preset.Name);

        Output.WriteTool($"{ActionName} preset \"{_preset.Name}\" saved.");
    }

    private void SaveAsPreset()
    {
        var name = PresetNameWindow.Prompt();

        if (string.IsNullOrWhiteSpace(name))
            return;

        CaptureIntoPreset(_preset);

        var copy = _preset.Clone();

        copy.Name = name;
        copy.Maps = MapSelection.SelectedMaps.ToList();

        var stored = Settings.PresetsFor(Kind);
        var existing = stored.FirstOrDefault(p => p.Name == name);

        if (existing is not null)
        {
            stored.Remove(existing);
            Presets.Remove(existing);
        }

        stored.Add(copy);
        Presets.Add(copy);

        SelectedPreset = copy;

        RaiseCommandStates();

        Output.WriteTool($"{ActionName} preset \"{name}\" created.");
    }

    private void DeletePreset()
    {
        if (Presets.Count <= 1 || _selectedPreset is null)
            return;

        var doomed = _selectedPreset;

        var confirm = MessageBox.Show(
            $"Delete the preset \"{doomed.Name}\"?",
            "FMFC Build Tool",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        Settings.PresetsFor(Kind).Remove(doomed);
        Presets.Remove(doomed);

        SelectedPreset = Presets[0];

        RaiseCommandStates();

        Output.WriteTool($"{ActionName} preset \"{doomed.Name}\" deleted.");
    }

    // ---------------------------------------------------------------- actions

    private void CopyCommandLine()
    {
        if (string.IsNullOrWhiteSpace(CommandPreview))
            return;

        try
        {
            Clipboard.SetText(CommandPreview);
            Output.WriteTool("Command copied to the clipboard.");
        }
        catch (Exception ex)
        {
            Output.WriteTool($"Could not copy to the clipboard: {ex.Message}", LogSeverity.Warning);
        }
    }

    private void SaveBatchFile()
    {
        if (Context.Engine is not { } engine)
            return;

        var maps = MapSelection.SelectedMaps;

        if (maps.Count == 0)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Batch file (*.bat)|*.bat",
            FileName = $"{Kind}-{Context.ProjectName}-{_preset.Name}.bat"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var script = BatchScriptWriter.ForEachMap(
                $"{ActionName}, preset \"{_preset.Name}\"",
                Context.ProjectDirectory,
                engine.EditorCmd,
                maps,
                PassesFor);

            File.WriteAllText(dialog.FileName, script);

            Output.WriteTool($"Saved {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            Output.WriteTool($"Could not save the batch file: {ex.Message}", LogSeverity.Error);
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// Recomputes validation and the command preview. Read-only with respect to the
    /// saved preset — <see cref="OnMapSelectionChanged"/> is the only writer.
    /// </summary>
    protected void Refresh()
    {
        var maps = MapSelection.SelectedMaps;

        var problems = new List<string>(ValidateInputs(maps));

        if (Context.HasProject && Context.Engine is null)
        {
            problems.Add(string.IsNullOrEmpty(Context.EngineError)
                ? "No Unreal Engine installation resolved."
                : Context.EngineError);
        }

        ValidationMessage = string.Join("  ·  ", problems);

        // Preview the first selected map: every map's invocations are identical apart
        // from the map, so showing its passes shows the shape of the whole run.
        CommandPreview = Context is { HasProject: true, Engine: { } engine } && maps.Count > 0
            ? Preview(engine, maps)
            : "";

        UpdateEstimate();

        OnPropertyChanged(nameof(CanRun));
        RaiseCommandStates();
    }

    private string Preview(EnginePaths engine, IReadOnlyList<string> maps)
    {
        var passes = PassesFor(maps[0]);

        var text = string.Join(
            Environment.NewLine,
            passes.Select(p => $"\"{engine.EditorCmd}\" {string.Join(" ", p.Arguments)}"));

        if (maps.Count == 1)
            return text;

        return text + Environment.NewLine + (passes.Count == 1
            ? $"… and {maps.Count - 1} more invocation(s), one per map."
            : $"… and {maps.Count - 1} more map(s), {passes.Count} invocations each.");
    }

    private void UpdateEstimate()
    {
        var estimate = Context.HasProject ? History.Estimate(Kind, Context.ProjectFile) : null;

        EstimateText = estimate is { } value ? $"~{value:hh\\:mm\\:ss} last time" : "";
    }

    private void RaiseCommandStates()
    {
        (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RetryFailedCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (DeletePresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
