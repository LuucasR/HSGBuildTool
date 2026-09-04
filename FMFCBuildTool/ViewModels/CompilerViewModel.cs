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
/// Compiles the project's C++ through UnrealBuildTool.
/// </summary>
/// <remarks>
/// Exists so a missing class fails in seconds rather than twenty minutes into a cook.
/// Being an <see cref="IBuildPage"/> it also queues, which is the point: Compile → Package
/// in one run.
///
/// Implements the interface directly rather than deriving from
/// <see cref="CommandletPageViewModel"/>, which loops over maps. This is one opaque
/// invocation, so it is the <see cref="PackageViewModel"/> shape, minus the presets.
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

    /// <summary>True: one UBT invocation reports no progress we can read.</summary>
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

    // ---------------------------------------------------------------- compile

    public async Task RunAsync()
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

        var commandLine = CompileBuilder.ToCommandLine(
            CompileBuilder.BuildArguments(_context.ProjectFile, Target, Configuration));

        IsCompiling = true;
        LastOutcome = null;

        _cancellation = new CancellationTokenSource();
        _elapsed.Restart();

        StatusText = "Compiling";

        _output.BeginSession("compile", _context.ProjectName, _context.ProjectFile);
        _output.WriteTool($"{engine.BuildBat} {commandLine}");

        var exitCode = -1;

        try
        {
            exitCode = await _runner.RunAsync(
                engine.BuildBat,
                commandLine,
                _context.ProjectDirectory,
                $"compile {Target} {Configuration}",
                _cancellation.Token);

            LastOutcome = _cancellation.IsCancellationRequested
                ? BuildOutcome.Stopped
                : exitCode == 0 ? BuildOutcome.Succeeded : BuildOutcome.Failed;

            // Build.bat's own bootstrap failures come back as 999 and a conflicting UBT as
            // 10; "Failed (999)" alone sends you to the log for something the status line
            // can just say.
            var reason = CompileBuilder.DescribeExitCode(exitCode);

            StatusText = LastOutcome switch
            {
                BuildOutcome.Succeeded => $"Succeeded in {_elapsed.Formatted}",
                BuildOutcome.Stopped => $"Stopped after {_elapsed.Formatted}",
                _ when reason.Length > 0 => $"Failed after {_elapsed.Formatted} — {reason}",
                _ => $"Failed ({exitCode}) after {_elapsed.Formatted}"
            };

            _output.WriteTool(
                $"Compile finished with exit code {exitCode} after {_elapsed.Formatted}.",
                LastOutcome == BuildOutcome.Succeeded ? LogSeverity.Info : LogSeverity.Error);
        }
        catch (Exception ex)
        {
            LastOutcome = BuildOutcome.Failed;
            StatusText = "Failed";

            _output.WriteTool($"Compile could not start: {ex.Message}", LogSeverity.Error);
        }
        finally
        {
            _elapsed.Stop();

            _cancellation?.Dispose();
            _cancellation = null;

            RecordHistory(exitCode);

            _output.EndSession();

            IsCompiling = false;

            UpdateEstimate();
        }
    }

    private void RecordHistory(int exitCode)
    {
        if (LastOutcome is not { } outcome)
            return;

        _history.Record(new BuildRecord
        {
            Kind = "compile",
            ProjectFile = _context.ProjectFile,
            ProjectName = _context.ProjectName,
            Detail = $"{Target} {Configuration}",
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

    /// <summary>Cancels the compile and kills Build.bat together with the UBT it spawned.</summary>
    private void Stop()
    {
        if (!_isCompiling)
            return;

        StatusText = "Stopping";

        _cancellation?.Cancel();
        _runner.Cancel();
    }

    // ---------------------------------------------------------------- actions

    private void CopyCommandLine()
    {
        if (string.IsNullOrWhiteSpace(CommandPreview))
            return;

        try
        {
            Clipboard.SetText(CommandPreview);
            _output.WriteTool("Command copied to the clipboard.");
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not copy to the clipboard: {ex.Message}", LogSeverity.Warning);
        }
    }

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
            var script = BatchScriptWriter.ForSingleCommand(
                $"Compile {Target} {Configuration}",
                _context.ProjectDirectory,
                engine.BuildBat,
                CompileBuilder.ToCommandLine(
                    CompileBuilder.BuildArguments(_context.ProjectFile, Target, Configuration)));

            File.WriteAllText(dialog.FileName, script);

            _output.WriteTool($"Saved {dialog.FileName}.");
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

        if (_context.HasProject && _context.Engine is null)
        {
            problems.Add(string.IsNullOrEmpty(_context.EngineError)
                ? "No Unreal Engine installation resolved."
                : _context.EngineError);
        }

        ValidationMessage = string.Join("  ·  ", problems);

        CommandPreview = _context is { HasProject: true, Engine: { } engine } && !string.IsNullOrWhiteSpace(Target)
            ? $"\"{engine.BuildBat}\" {CompileBuilder.ToCommandLine(CompileBuilder.BuildArguments(_context.ProjectFile, Target, Configuration))}"
            : "";

        UpdateEstimate();

        OnPropertyChanged(nameof(CanRun));

        RaiseCommandStates();
    }

    private void UpdateEstimate()
    {
        var estimate = _context.HasProject ? _history.Estimate("compile", _context.ProjectFile) : null;

        EstimateText = estimate is { } value ? $"~{value:hh\\:mm\\:ss} last time" : "";
    }

    private void RaiseCommandStates()
    {
        (RunCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveBatchFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
