using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Shared behaviour for the pages that drive UnrealEditor-Cmd commandlets over a set
/// of maps (Navigation, Lighting): map selection, validation, command preview, and a
/// sequential per-map run with progress and a failure summary.
/// </summary>
/// <remarks>
/// The per-map loop is the important part. The old navigation code passed every
/// selected map to one invocation as space-separated positional arguments, which the
/// commandlet cannot consume — only the first map was ever built, and the rest were
/// reported as successful.
/// </remarks>
public abstract class CommandletPageViewModel : ObservableObject
{
    private readonly Stopwatch _stopwatch = new();

    private CancellationTokenSource? _cancellation;
    private string _commandPreview = "";
    private string _validationMessage = "";
    private string _statusText = "Ready";
    private double _progress;
    private bool _isRunning;
    private bool _suspendWrite;

    protected CommandletPageViewModel(BuildContext context, ProcessRunner runner, OutputService output, AppConfig config)
    {
        Context = context;
        Runner = runner;
        Output = output;
        Config = config;

        MapSelection = new MapSelectionViewModel();
        MapSelection.SelectionChanged += OnMapSelectionChanged;

        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        CancelCommand = new RelayCommand(Cancel, () => _isRunning);
        CopyCommandLineCommand = new RelayCommand(CopyCommandLine);

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

    protected ProjectSettings Settings { get; private set; } = new();

    public MapSelectionViewModel MapSelection { get; }

    public ICommand RunCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyCommandLineCommand { get; }

    /// <summary>Verb shown on the primary button, e.g. "BUILD NAVIGATION".</summary>
    public abstract string RunButtonText { get; }

    /// <summary>Short suffix for the on-disk log file name.</summary>
    protected abstract string SessionLabel { get; }

    /// <summary>Human-readable name of the operation, used in log lines.</summary>
    protected abstract string ActionName { get; }

    protected abstract IReadOnlyList<string> ArgumentsFor(string map);

    protected abstract IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps);

    protected abstract IReadOnlyList<string> ReadSavedSelection(ProjectSettings settings);

    protected abstract void WriteSelection(ProjectSettings settings, IReadOnlyList<string> maps);

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
        protected set => SetProperty(ref _statusText, value);
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

    public async Task OnProjectChangedAsync()
    {
        if (!Context.HasProject)
        {
            Refresh();
            return;
        }

        // Suspended while loading: scanning raises SelectionChanged, and writing the
        // still-empty selection back into the settings would erase the saved one.
        _suspendWrite = true;

        try
        {
            Settings = Config.GetOrCreate(Context.ProjectFile);

            await MapSelection.LoadAsync(Context.ProjectFile, ReadSavedSelection(Settings));
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
            WriteSelection(Settings, MapSelection.SelectedMaps);

        Refresh();
    }

    private async Task RunAsync()
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
            Output.WriteTool($"A build is already running ({Runner.CurrentDescription}). Cancel it first.", LogSeverity.Warning);
            return;
        }

        IsRunning = true;

        _cancellation = new CancellationTokenSource();
        _stopwatch.Restart();

        Output.BeginSession(SessionLabel, Context.ProjectName);
        Output.WriteTool($"Starting {ActionName} for {maps.Count} map(s).");

        var failed = new List<string>();
        var cancelled = false;

        try
        {
            for (var i = 0; i < maps.Count; i++)
            {
                if (_cancellation.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var map = maps[i];

                StatusText = $"{ActionName}: {i + 1} of {maps.Count} — {map}";
                Progress = i * 100.0 / maps.Count;

                var commandLine = string.Join(" ", ArgumentsFor(map));

                Output.WriteTool($"[{i + 1}/{maps.Count}] {engine.EditorCmd} {commandLine}");

                int exitCode;

                try
                {
                    exitCode = await Runner.RunAsync(
                        engine.EditorCmd,
                        commandLine,
                        Context.ProjectDirectory,
                        $"{ActionName} ({map})",
                        _cancellation.Token);
                }
                catch (Exception ex)
                {
                    Output.WriteTool($"{map}: could not start — {ex.Message}", LogSeverity.Error);
                    failed.Add(map);

                    continue;
                }

                if (_cancellation.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                if (exitCode == 0)
                {
                    Output.WriteTool($"{map}: OK");
                }
                else
                {
                    Output.WriteTool($"{map}: FAILED (exit code {exitCode})", LogSeverity.Error);
                    failed.Add(map);
                }
            }

            Progress = 100;

            WriteSummary(maps, failed, cancelled);
        }
        finally
        {
            _stopwatch.Stop();

            _cancellation?.Dispose();
            _cancellation = null;

            Output.EndSession();

            IsRunning = false;
            Progress = 0;
        }
    }

    private void WriteSummary(IReadOnlyList<string> maps, IReadOnlyList<string> failed, bool cancelled)
    {
        var elapsed = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        var succeeded = maps.Count - failed.Count;

        if (cancelled)
        {
            StatusText = $"Cancelled after {elapsed}";
            Output.WriteTool($"{ActionName} cancelled after {elapsed}. {succeeded} map(s) completed.", LogSeverity.Warning);

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

    private void Cancel()
    {
        if (!_isRunning)
            return;

        StatusText = "Cancelling";

        _cancellation?.Cancel();
        Runner.Cancel();
    }

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

    /// <summary>
    /// Recomputes validation and the command preview. Read-only with respect to the
    /// saved settings — <see cref="OnMapSelectionChanged"/> is the only writer.
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

        // Preview the first selected map: every invocation is identical apart from the map.
        CommandPreview = Context is { HasProject: true, Engine: { } engine } && maps.Count > 0
            ? $"\"{engine.EditorCmd}\" {string.Join(" ", ArgumentsFor(maps[0]))}" +
              (maps.Count > 1 ? $"{Environment.NewLine}… and {maps.Count - 1} more invocation(s), one per map." : "")
            : "";

        OnPropertyChanged(nameof(CanRun));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
