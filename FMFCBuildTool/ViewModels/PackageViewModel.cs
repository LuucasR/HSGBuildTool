using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.Views;
using Ookii.Dialogs.Wpf;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// The BuildCookRun page. Every option is a bindable property over the active
/// <see cref="BuildPreset"/>, replacing the two 55-line methods that used to copy
/// ~30 checkboxes between the controls and the model by hand.
/// </summary>
public sealed class PackageViewModel : ObservableObject
{
    private static readonly (string Code, string Label)[] KnownCultures =
    {
        ("en", "English"), ("es", "Spanish"), ("es-419", "Spanish (Latin America)"),
        ("pt-BR", "Portuguese (Brazil)"), ("fr", "French"), ("de", "German"),
        ("it", "Italian"), ("ru", "Russian"), ("pl", "Polish"), ("tr", "Turkish"),
        ("ja", "Japanese"), ("ko", "Korean"), ("zh-Hans", "Chinese (Simplified)"),
        ("zh-Hant", "Chinese (Traditional)"), ("ar", "Arabic")
    };

    private readonly BuildContext _context;
    private readonly ProcessRunner _runner;
    private readonly OutputService _output;
    private readonly AppConfig _config;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _stopwatch = new();

    private ProjectSettings _settings = new();
    private BuildPreset _preset = new();
    private CancellationTokenSource? _cancellation;

    private string _commandPreview = "";
    private string _validationMessage = "";
    private string _statusText = "Ready";
    private string _elapsedText = "";
    private bool _isBuilding;
    private bool _suspendRefresh;

    public PackageViewModel(BuildContext context, ProcessRunner runner, OutputService output, AppConfig config)
    {
        _context = context;
        _runner = runner;
        _output = output;
        _config = config;

        MapSelection = new MapSelectionViewModel();
        MapSelection.SelectionChanged += OnMapSelectionChanged;

        foreach (var (code, label) in KnownCultures)
        {
            var option = new CultureOption(code, label);
            option.Changed += OnCultureChanged;

            Cultures.Add(option);
        }

        BuildCommand = new AsyncRelayCommand(BuildAsync, () => CanBuild);
        CancelCommand = new RelayCommand(Cancel, () => _isBuilding);
        BrowseArchiveCommand = new RelayCommand(BrowseArchive);
        CopyCommandLineCommand = new RelayCommand(CopyCommandLine);
        SaveBatchFileCommand = new RelayCommand(SaveBatchFile);

        SavePresetCommand = new RelayCommand(SavePreset);
        SaveAsPresetCommand = new RelayCommand(SaveAsPreset);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => Presets.Count > 1);

        _timer.Tick += (_, _) => ElapsedText = _stopwatch.Elapsed.ToString(@"hh\:mm\:ss");

        _context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BuildContext.ProjectFile) or nameof(BuildContext.Engine))
                Refresh();
        };
    }

    public MapSelectionViewModel MapSelection { get; }

    public ObservableCollection<CultureOption> Cultures { get; } = new();

    public ObservableCollection<BuildPreset> Presets { get; } = new();

    public IReadOnlyList<string> Platforms { get; } = new[] { "Win64", "Linux", "Mac", "Android", "IOS" };

    public IReadOnlyList<string> Configurations { get; } = new[] { "Shipping", "Development", "DebugGame", "Test" };

    public ICommand BuildCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseArchiveCommand { get; }
    public ICommand CopyCommandLineCommand { get; }
    public ICommand SaveBatchFileCommand { get; }
    public ICommand SavePresetCommand { get; }
    public ICommand SaveAsPresetCommand { get; }
    public ICommand DeletePresetCommand { get; }

    // ---------------------------------------------------------------- presets

    private BuildPreset? _selectedPreset;

    public BuildPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || value is null)
                return;

            _preset = value;
            _settings.ActivePreset = value.Name;

            LoadPresetIntoUi();
        }
    }

    // ---------------------------------------------------------------- target

    public string Platform
    {
        get => _preset.Platform;
        set => SetOption(_preset.Platform, value, v => _preset.Platform = v);
    }

    public string Configuration
    {
        get => _preset.Configuration;
        set => SetOption(_preset.Configuration, value, v => _preset.Configuration = v);
    }

    public bool Client
    {
        get => _preset.Client;
        set => SetOption(_preset.Client, value, v => _preset.Client = v);
    }

    public bool Server
    {
        get => _preset.Server;
        set => SetOption(_preset.Server, value, v => _preset.Server = v);
    }

    // ---------------------------------------------------------------- pipeline

    public bool Build
    {
        get => _preset.Build;
        set => SetOption(_preset.Build, value, v => _preset.Build = v);
    }

    public bool Cook
    {
        get => _preset.Cook;
        set => SetOption(_preset.Cook, value, v => _preset.Cook = v);
    }

    public bool Stage
    {
        get => _preset.Stage;
        set => SetOption(_preset.Stage, value, v => _preset.Stage = v);
    }

    public bool Package
    {
        get => _preset.Package;
        set => SetOption(_preset.Package, value, v => _preset.Package = v);
    }

    public bool Archive
    {
        get => _preset.Archive;
        set => SetOption(_preset.Archive, value, v => _preset.Archive = v);
    }

    public string ArchiveDirectory
    {
        get => _preset.ArchiveDirectory;
        set => SetOption(_preset.ArchiveDirectory, value, v => _preset.ArchiveDirectory = v);
    }

    // ---------------------------------------------------------------- cook

    /// <summary>Bound to the "Modified" radio; FullCook is its inverse.</summary>
    public bool ModifiedCook
    {
        get => !_preset.FullCook;
        set
        {
            if (value == !_preset.FullCook)
                return;

            _preset.FullCook = !value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(FullCook));
            Refresh();
        }
    }

    public bool FullCook
    {
        get => _preset.FullCook;
        set
        {
            if (value == _preset.FullCook)
                return;

            _preset.FullCook = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ModifiedCook));
            Refresh();
        }
    }

    public bool CookIncremental
    {
        get => _preset.CookIncremental;
        set => SetOption(_preset.CookIncremental, value, v => _preset.CookIncremental = v);
    }

    public bool ZenStore
    {
        get => _preset.ZenStore;
        set => SetOption(_preset.ZenStore, value, v => _preset.ZenStore = v);
    }

    public bool SkipCookingEditorContent
    {
        get => _preset.SkipCookingEditorContent;
        set => SetOption(_preset.SkipCookingEditorContent, value, v => _preset.SkipCookingEditorContent = v);
    }

    public bool UnversionedCookedContent
    {
        get => _preset.UnversionedCookedContent;
        set => SetOption(_preset.UnversionedCookedContent, value, v => _preset.UnversionedCookedContent = v);
    }

    // ---------------------------------------------------------------- packaging

    public bool Pak
    {
        get => _preset.Pak;
        set => SetOption(_preset.Pak, value, v => _preset.Pak = v);
    }

    /// <summary>Was hardcoded to false in the old code-behind, so -iostore was unreachable from the UI.</summary>
    public bool IoStore
    {
        get => _preset.IoStore;
        set => SetOption(_preset.IoStore, value, v => _preset.IoStore = v);
    }

    public bool Compressed
    {
        get => _preset.Compressed;
        set => SetOption(_preset.Compressed, value, v => _preset.Compressed = v);
    }

    public bool Prereqs
    {
        get => _preset.Prereqs;
        set => SetOption(_preset.Prereqs, value, v => _preset.Prereqs = v);
    }

    public bool Distribution
    {
        get => _preset.Distribution;
        set => SetOption(_preset.Distribution, value, v => _preset.Distribution = v);
    }

    public bool CrashReporter
    {
        get => _preset.CrashReporter;
        set => SetOption(_preset.CrashReporter, value, v => _preset.CrashReporter = v);
    }

    // ---------------------------------------------------------------- advanced

    public bool NoCompile
    {
        get => _preset.NoCompile;
        set => SetOption(_preset.NoCompile, value, v => _preset.NoCompile = v);
    }

    public bool NoCompileEditor
    {
        get => _preset.NoCompileEditor;
        set => SetOption(_preset.NoCompileEditor, value, v => _preset.NoCompileEditor = v);
    }

    public bool FileOpenLog
    {
        get => _preset.FileOpenLog;
        set => SetOption(_preset.FileOpenLog, value, v => _preset.FileOpenLog = v);
    }

    public bool StdOut
    {
        get => _preset.StdOut;
        set => SetOption(_preset.StdOut, value, v => _preset.StdOut = v);
    }

    public bool CrashForUAT
    {
        get => _preset.CrashForUAT;
        set => SetOption(_preset.CrashForUAT, value, v => _preset.CrashForUAT = v);
    }

    public bool Unattended
    {
        get => _preset.Unattended;
        set => SetOption(_preset.Unattended, value, v => _preset.Unattended = v);
    }

    public bool NoLogTimes
    {
        get => _preset.NoLogTimes;
        set => SetOption(_preset.NoLogTimes, value, v => _preset.NoLogTimes = v);
    }

    public bool UseProjectDefaultMaps
    {
        get => _preset.UseProjectDefaultMaps;
        set => SetOption(_preset.UseProjectDefaultMaps, value, v => _preset.UseProjectDefaultMaps = v);
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

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (SetProperty(ref _isBuilding, value))
                RaiseCommandStates();
        }
    }

    public bool CanBuild => !_isBuilding && !_runner.IsRunning && ValidationMessage.Length == 0;

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Called when the active project changes; rebinds presets and the map list.</summary>
    public async Task OnProjectChangedAsync()
    {
        if (!_context.HasProject)
        {
            Presets.Clear();
            Refresh();
            return;
        }

        // Suspended for the whole reload: scanning the maps raises SelectionChanged, and
        // writing the (still empty) UI state back into the preset at that point would
        // wipe the saved cultures and map selection before they have been applied.
        _suspendRefresh = true;

        try
        {
            _settings = _config.GetOrCreate(_context.ProjectFile);

            Presets.Clear();

            foreach (var preset in _settings.Presets)
                Presets.Add(preset);

            _selectedPreset = _settings.GetActivePreset();
            _preset = _selectedPreset;

            OnPropertyChanged(nameof(SelectedPreset));

            await MapSelection.LoadAsync(_context.ProjectFile, _preset.Maps);
        }
        finally
        {
            _suspendRefresh = false;
        }

        LoadPresetIntoUi();
    }

    private void LoadPresetIntoUi()
    {
        _suspendRefresh = true;

        try
        {
            foreach (var culture in Cultures)
                culture.Selected = _preset.CookCultures.Contains(culture.Code, StringComparer.OrdinalIgnoreCase);

            MapSelection.ApplySelection(_preset.Maps);
        }
        finally
        {
            _suspendRefresh = false;
        }

        // Empty name means "everything changed" — cheaper and less error-prone than
        // listing thirty property names after a preset swap.
        OnPropertyChanged(string.Empty);

        Refresh();
    }

    // ---------------------------------------------------------------- build

    private async Task BuildAsync()
    {
        if (!_context.HasProject)
            return;

        CaptureSelectionIntoPreset();

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
            _output.WriteTool($"A build is already running ({_runner.CurrentDescription}). Cancel it first.", LogSeverity.Warning);
            return;
        }

        var arguments = RunUATBuilder.BuildArguments(_preset, _context.ProjectFile, engine);
        var commandLine = RunUATBuilder.ToCommandLine(arguments);

        IsBuilding = true;

        _cancellation = new CancellationTokenSource();
        _stopwatch.Restart();
        _timer.Start();

        StatusText = "Building";
        ElapsedText = "00:00:00";

        _output.BeginSession("package", _context.ProjectName);
        _output.WriteTool($"{engine.RunUAT} {commandLine}");

        try
        {
            var exitCode = await _runner.RunAsync(
                engine.RunUAT,
                commandLine,
                _context.ProjectDirectory,
                "package build",
                _cancellation.Token);

            StatusText = exitCode == 0
                ? $"Succeeded in {_stopwatch.Elapsed:hh\\:mm\\:ss}"
                : $"Failed ({exitCode}) after {_stopwatch.Elapsed:hh\\:mm\\:ss}";

            _output.WriteTool(
                $"Package build finished with exit code {exitCode} after {_stopwatch.Elapsed:hh\\:mm\\:ss}.",
                exitCode == 0 ? LogSeverity.Info : LogSeverity.Error);
        }
        catch (Exception ex)
        {
            // Goes to the log panel rather than a MessageBox full of stack trace.
            StatusText = "Failed";
            _output.WriteTool($"Package build could not start: {ex.Message}", LogSeverity.Error);
        }
        finally
        {
            _timer.Stop();
            _stopwatch.Stop();

            _cancellation?.Dispose();
            _cancellation = null;

            _output.EndSession();

            IsBuilding = false;
            ElapsedText = "";
        }
    }

    private void Cancel()
    {
        if (!_isBuilding)
            return;

        StatusText = "Cancelling";

        _cancellation?.Cancel();
        _runner.Cancel();
    }

    /// <summary>
    /// Copies the live map selection into the preset. Done immediately before building
    /// and before saving, so what is on screen is what gets built and stored.
    /// </summary>
    private void CaptureSelectionIntoPreset()
    {
        _preset.Maps = MapSelection.SelectedMaps.ToList();

        _preset.CookCultures = Cultures
            .Where(c => c.Selected)
            .Select(c => c.Code)
            .ToList();
    }

    // ---------------------------------------------------------------- presets

    private void SavePreset()
    {
        CaptureSelectionIntoPreset();

        _settings.ActivePreset = _preset.Name;

        _output.WriteTool($"Preset \"{_preset.Name}\" saved.");
    }

    private void SaveAsPreset()
    {
        var name = PromptForPresetName();

        if (string.IsNullOrWhiteSpace(name))
            return;

        CaptureSelectionIntoPreset();

        var copy = _preset.Clone();
        copy.Name = name;

        var existing = _settings.Presets.FirstOrDefault(p => p.Name == name);

        if (existing is not null)
        {
            _settings.Presets.Remove(existing);
            Presets.Remove(existing);
        }

        _settings.Presets.Add(copy);
        Presets.Add(copy);

        SelectedPreset = copy;

        RaiseCommandStates();

        _output.WriteTool($"Preset \"{name}\" created.");
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

        _settings.Presets.Remove(doomed);
        Presets.Remove(doomed);

        SelectedPreset = Presets[0];

        RaiseCommandStates();

        _output.WriteTool($"Preset \"{doomed.Name}\" deleted.");
    }

    private static string? PromptForPresetName()
    {
        var dialog = new PresetNameWindow
        {
            Owner = Application.Current.MainWindow
        };

        return dialog.ShowDialog() == true ? dialog.PresetName : null;
    }

    // ---------------------------------------------------------------- actions

    private void BrowseArchive()
    {
        var dialog = new VistaFolderBrowserDialog
        {
            SelectedPath = string.IsNullOrWhiteSpace(ArchiveDirectory)
                ? _config.DefaultArchiveRoot
                : ArchiveDirectory
        };

        if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            ArchiveDirectory = dialog.SelectedPath;
    }

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
            FileName = $"build-{_context.ProjectName}-{_preset.Name}.bat"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var script =
                $"@echo off{Environment.NewLine}" +
                $"REM Generated by FMFC Build Tool — preset \"{_preset.Name}\"{Environment.NewLine}" +
                $"cd /d \"{_context.ProjectDirectory}\"{Environment.NewLine}" +
                $"call \"{engine.RunUAT}\" {RunUATBuilder.ToCommandLine(RunUATBuilder.BuildArguments(_preset, _context.ProjectFile, engine))}{Environment.NewLine}";

            File.WriteAllText(dialog.FileName, script);

            _output.WriteTool($"Saved {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not save the batch file: {ex.Message}", LogSeverity.Error);
        }
    }

    // ---------------------------------------------------------------- plumbing

    private void OnCultureChanged()
    {
        if (_suspendRefresh)
            return;

        _preset.CookCultures = Cultures.Where(c => c.Selected).Select(c => c.Code).ToList();

        Refresh();
    }

    private void OnMapSelectionChanged()
    {
        if (_suspendRefresh)
            return;

        _preset.Maps = MapSelection.SelectedMaps.ToList();

        Refresh();
    }

    private void SetOption<T>(T current, T value, Action<T> assign, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;

        assign(value);

        OnPropertyChanged(property);
        Refresh();
    }

    /// <summary>
    /// Recomputes validation and the command preview. Read-only with respect to the
    /// preset — the option setters, <see cref="OnCultureChanged"/> and
    /// <see cref="OnMapSelectionChanged"/> are the only writers.
    /// </summary>
    private void Refresh()
    {
        if (_suspendRefresh)
            return;

        var problems = new List<string>(RunUATBuilder.Validate(_preset, _context.ProjectFile));

        if (_context.HasProject && _context.Engine is null)
        {
            problems.Add(string.IsNullOrEmpty(_context.EngineError)
                ? "No Unreal Engine installation resolved."
                : _context.EngineError);
        }

        ValidationMessage = string.Join("  ·  ", problems);

        CommandPreview = _context is { HasProject: true, Engine: { } engine }
            ? $"\"{engine.RunUAT}\" {RunUATBuilder.ToCommandLine(RunUATBuilder.BuildArguments(_preset, _context.ProjectFile, engine))}"
            : "";

        OnPropertyChanged(nameof(CanBuild));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (BuildCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeletePresetCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
