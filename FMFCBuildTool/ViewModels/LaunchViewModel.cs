using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// Runs the project as a game — standalone, or a server with clients — straight from the
/// editor binaries, without opening the editor.
/// </summary>
/// <remarks>
/// Not an <see cref="IBuildPage"/>: launching is not a build step. It does not queue, it
/// has no outcome to record in history, and it must not hold the runner the build pages
/// wait on, so it has its own <see cref="GameLauncher"/>. Options are plain properties on
/// <see cref="ProjectSettings"/>, the Compiler page's pattern — there is nothing here
/// worth naming as a preset.
/// </remarks>
public sealed class LaunchViewModel : ObservableObject
{
    /// <summary>The first entry in the map list: no map on the command line at all.</summary>
    public const string DefaultMapEntry = "(Project default map)";

    private readonly BuildContext _context;
    private readonly GameLauncher _launcher;
    private readonly OutputService _output;
    private readonly AppConfig _config;

    private ProjectSettings _settings = new();
    private string _commandPreview = "";
    private string _validationMessage = "";
    private bool _isScanning;
    private string _mapSearch = "";

    public LaunchViewModel(BuildContext context, GameLauncher launcher, OutputService output, AppConfig config)
    {
        _context = context;
        _launcher = launcher;
        _output = output;
        _config = config;

        FilteredMaps = new ListCollectionView(Maps) { Filter = item => IsMapVisible((string)item) };

        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => CanLaunch);
        StopAllCommand = new RelayCommand(_launcher.StopAll, () => IsRunning);
        CopyCommandLineCommand = new RelayCommand(CopyCommandLine, () => CommandPreview.Length > 0);
        SaveBatchFileCommand = new RelayCommand(SaveBatchFile, () => CommandPreview.Length > 0);
        RescanMapsCommand = new AsyncRelayCommand(ScanMapsAsync);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder, () => _context.HasProject);

        // Exits arrive on a worker thread; the commands' CanExecuteChanged must not.
        _launcher.RunningChanged += () => Application.Current?.Dispatcher.BeginInvoke(RaiseRunningState);
        _launcher.Instances.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasInstances));

        _context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BuildContext.ProjectFile) or nameof(BuildContext.Engine))
                Refresh();
        };
    }

    // ---------------------------------------------------------------- lists

    /// <summary>/Game paths, with <see cref="DefaultMapEntry"/> first.</summary>
    public ObservableCollection<string> Maps { get; } = new() { DefaultMapEntry };

    /// <summary>
    /// <see cref="Maps"/> filtered by <see cref="MapSearch"/>, which the page lists. Its own
    /// view rather than the default one, so nothing else bound to <see cref="Maps"/> filters too.
    /// </summary>
    public ICollectionView FilteredMaps { get; }

    /// <summary>
    /// Every word must appear somewhere in the /Game path, in any order: "arena night"
    /// finds /Game/Maps/Arena/L_Arena_Night. With hundreds of maps a single substring is
    /// rarely enough to get down to one.
    /// </summary>
    public string MapSearch
    {
        get => _mapSearch;
        set
        {
            if (!SetProperty(ref _mapSearch, value ?? ""))
                return;

            FilteredMaps.Refresh();

            OnPropertyChanged(nameof(MapSummary));
        }
    }

    /// <summary>
    /// The first map the search matches, for Enter in the search box. Not simply the first
    /// row: the default entry and the current selection are listed whether they match or not.
    /// </summary>
    public string? FirstSearchMatch
        => string.IsNullOrWhiteSpace(_mapSearch)
            ? null
            : Maps.FirstOrDefault(m => m != DefaultMapEntry && MatchesSearch(m));

    /// <summary>"12 of 340 maps" while searching, "340 maps" otherwise.</summary>
    public string MapSummary
    {
        get
        {
            // The default-map entry is not a map, and always shown; it is not counted.
            var total = Maps.Count - 1;

            if (string.IsNullOrWhiteSpace(_mapSearch))
                return total == 1 ? "1 map" : $"{total} maps";

            var shown = FilteredMaps.Cast<string>().Count(m => m != DefaultMapEntry && MatchesSearch(m));

            return $"{shown} of {total} maps";
        }
    }

    public IReadOnlyList<LaunchModeOption> Modes => LaunchBuilder.Modes;

    public IReadOnlyList<string> Rhis => LaunchBuilder.Rhis;

    public IReadOnlyList<string> ScalabilityLevels => LaunchBuilder.ScalabilityLevels;

    public ObservableCollection<GameInstance> Instances => _launcher.Instances;

    public bool HasInstances => Instances.Count > 0;

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    // ---------------------------------------------------------------- options

    public string Map
    {
        get => string.IsNullOrWhiteSpace(_settings.LaunchMap) ? DefaultMapEntry : _settings.LaunchMap;
        set
        {
            if (value is null)
                return;

            Set(value == DefaultMapEntry ? "" : value, _settings.LaunchMap, v => _settings.LaunchMap = v);
        }
    }

    public LaunchMode Mode
    {
        get => Enum.TryParse<LaunchMode>(_settings.LaunchMode, out var mode) ? mode : LaunchMode.Standalone;
        set
        {
            if (Mode == value)
                return;

            _settings.LaunchMode = value.ToString();

            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowsClients));
            OnPropertyChanged(nameof(ShowsPort));
            OnPropertyChanged(nameof(ShowsConnectAddress));
            OnPropertyChanged(nameof(ModeDescription));

            Refresh();
        }
    }

    public string ModeDescription => Modes.First(m => m.Id == Mode).Description;

    public bool ShowsClients => LaunchBuilder.HasClients(Mode);

    public bool ShowsPort => Mode != LaunchMode.Standalone;

    public bool ShowsConnectAddress => Mode == LaunchMode.ClientOnly;

    public int ClientCount
    {
        get => _settings.LaunchClientCount;
        set => Set(Math.Clamp(value, 1, LaunchBuilder.MaxClients), _settings.LaunchClientCount, v => _settings.LaunchClientCount = v);
    }

    public IReadOnlyList<int> ClientCounts { get; } = Enumerable.Range(1, LaunchBuilder.MaxClients).ToArray();

    public int Port
    {
        get => _settings.LaunchPort;
        set => Set(value, _settings.LaunchPort, v => _settings.LaunchPort = v);
    }

    public string ConnectAddress
    {
        get => _settings.LaunchConnectAddress;
        set => Set(value ?? "", _settings.LaunchConnectAddress, v => _settings.LaunchConnectAddress = v);
    }

    public bool Windowed
    {
        get => _settings.LaunchWindowed;
        set => Set(value, _settings.LaunchWindowed, v => _settings.LaunchWindowed = v);
    }

    public int ResX
    {
        get => _settings.LaunchResX;
        set => Set(value, _settings.LaunchResX, v => _settings.LaunchResX = v);
    }

    public int ResY
    {
        get => _settings.LaunchResY;
        set => Set(value, _settings.LaunchResY, v => _settings.LaunchResY = v);
    }

    public bool TileWindows
    {
        get => _settings.LaunchTileWindows;
        set => Set(value, _settings.LaunchTileWindows, v => _settings.LaunchTileWindows = v);
    }

    public string GameMode
    {
        get => _settings.LaunchGameMode;
        set => Set(value ?? "", _settings.LaunchGameMode, v => _settings.LaunchGameMode = v);
    }

    public string UrlOptions
    {
        get => _settings.LaunchUrlOptions;
        set => Set(value ?? "", _settings.LaunchUrlOptions, v => _settings.LaunchUrlOptions = v);
    }

    public string Rhi
    {
        get => LaunchBuilder.Rhis.Contains(_settings.LaunchRhi) ? _settings.LaunchRhi : "Default";
        set => Set(value ?? "Default", _settings.LaunchRhi, v => _settings.LaunchRhi = v);
    }

    public string Scalability
    {
        get => LaunchBuilder.ScalabilityLevels.Contains(_settings.LaunchScalability) ? _settings.LaunchScalability : "Default";
        set => Set(value ?? "Default", _settings.LaunchScalability, v => _settings.LaunchScalability = v);
    }

    public bool NoSound
    {
        get => _settings.LaunchNoSound;
        set => Set(value, _settings.LaunchNoSound, v => _settings.LaunchNoSound = v);
    }

    public bool ShowLogConsole
    {
        get => _settings.LaunchShowLogConsole;
        set => Set(value, _settings.LaunchShowLogConsole, v => _settings.LaunchShowLogConsole = v);
    }

    public string ExecCmds
    {
        get => _settings.LaunchExecCmds;
        set => Set(value ?? "", _settings.LaunchExecCmds, v => _settings.LaunchExecCmds = v);
    }

    public bool NoSteam
    {
        get => _settings.LaunchNoSteam;
        set => Set(value, _settings.LaunchNoSteam, v => _settings.LaunchNoSteam = v);
    }

    public string ExtraArguments
    {
        get => _settings.LaunchExtraArguments;
        set => Set(value ?? "", _settings.LaunchExtraArguments, v => _settings.LaunchExtraArguments = v);
    }

    // ---------------------------------------------------------------- state

    public ICommand LaunchCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand CopyCommandLineCommand { get; }
    public ICommand SaveBatchFileCommand { get; }
    public ICommand RescanMapsCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public bool IsRunning => _launcher.IsRunning;

    public bool CanLaunch => !IsRunning && _context is { HasProject: true, Engine: not null } && !HasValidationMessage;

    public string StatusText
    {
        get
        {
            var running = Instances.Count(i => i.IsRunning);

            return running switch
            {
                0 when Instances.Count > 0 => "All instances have exited.",
                0 => "Ready",
                1 => "1 instance running.",
                _ => $"{running} instances running."
            };
        }
    }

    public string CommandPreview
    {
        get => _commandPreview;
        private set
        {
            if (!SetProperty(ref _commandPreview, value))
                return;

            (CopyCommandLineCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    public bool HasValidationMessage => ValidationMessage.Length > 0;

    // ---------------------------------------------------------------- lifecycle

    public async Task OnProjectChangedAsync()
    {
        _settings = _context.HasProject ? _config.GetOrCreate(_context.ProjectFile) : new ProjectSettings();

        foreach (var property in new[]
                 {
                     nameof(Mode), nameof(ModeDescription), nameof(ShowsClients), nameof(ShowsPort),
                     nameof(ShowsConnectAddress), nameof(ClientCount), nameof(Port), nameof(ConnectAddress),
                     nameof(Windowed), nameof(ResX), nameof(ResY), nameof(TileWindows), nameof(GameMode),
                     nameof(UrlOptions), nameof(Rhi), nameof(Scalability), nameof(NoSound), nameof(ShowLogConsole),
                     nameof(ExecCmds), nameof(NoSteam), nameof(ExtraArguments)
                 })
        {
            OnPropertyChanged(property);
        }

        await ScanMapsAsync();

        Refresh();
    }

    /// <summary>
    /// The default entry and the current selection always stay in the list, so a search
    /// never leaves the page showing no selection for a map that is still the one to launch.
    /// </summary>
    private bool IsMapVisible(string map)
        => map == DefaultMapEntry || map == Map || MatchesSearch(map);

    private bool MatchesSearch(string map)
        => _mapSearch
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => map.Contains(word, StringComparison.OrdinalIgnoreCase));

    private async Task ScanMapsAsync()
    {
        IsScanning = true;

        try
        {
            var maps = _context.HasProject
                ? await MapScanner.ScanAsync(_context.ProjectFile)
                : new List<MapItem>();

            Maps.Clear();
            Maps.Add(DefaultMapEntry);

            foreach (var map in maps)
                Maps.Add(map.RelativePath);

            // A saved map that has since been deleted or moved goes back to the default
            // rather than launching something that is not there.
            if (_settings.LaunchMap.Length > 0 && !Maps.Contains(_settings.LaunchMap))
            {
                _output.WriteTool($"Launch: the saved map {_settings.LaunchMap} is no longer in Content.", LogSeverity.Warning);
                _settings.LaunchMap = "";
            }

            OnPropertyChanged(nameof(Map));
            OnPropertyChanged(nameof(MapSummary));
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ---------------------------------------------------------------- actions

    private LaunchOptions Options => new(
        Mode,
        _settings.LaunchMap,
        ClientCount,
        Port,
        ConnectAddress,
        Windowed,
        ResX,
        ResY,
        TileWindows,
        GameMode,
        UrlOptions,
        Rhi,
        NoSound,
        ShowLogConsole,
        ExecCmds,
        NoSteam,
        ExtraArguments,
        Scalability);

    private static ScreenArea Screen()
    {
        var area = SystemParameters.WorkArea;

        return new ScreenArea((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height);
    }

    private IReadOnlyList<LaunchInstance> InstancesToLaunch()
        => LaunchBuilder.Build(_context.ProjectFile, Options, Screen());

    private async Task LaunchAsync()
    {
        if (_context.Engine is not { } engine || !CanLaunch)
            return;

        var launches = InstancesToLaunch();

        _output.WriteTool($"Launching {_context.ProjectName}: {Modes.First(m => m.Id == Mode).Display}, {launches.Count} process(es).");

        try
        {
            await _launcher.LaunchAsync(engine.Editor, _context.ProjectDirectory, launches);
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Launch failed: {ex.Message}", LogSeverity.Error);
        }

        RaiseRunningState();
    }

    private void CopyCommandLine()
    {
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

    /// <summary>
    /// A .bat that starts every instance with <c>start</c>, so they run side by side the way
    /// the page runs them, with the same head start for the server.
    /// </summary>
    private void SaveBatchFile()
    {
        if (_context.Engine is not { } engine)
            return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Batch file (*.bat)|*.bat",
            FileName = $"launch-{_context.ProjectName}-{Mode}.bat"
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var script = new System.Text.StringBuilder();

            script.AppendLine("@echo off");
            script.AppendLine($"REM Launch {_context.ProjectName} - {Modes.First(m => m.Id == Mode).Display}");
            script.AppendLine($"REM Generated by FMFC Build Tool on {DateTime.Now:yyyy-MM-dd HH:mm}");
            script.AppendLine($"cd /d \"{_context.ProjectDirectory}\"");
            script.AppendLine($"if not exist \"{LaunchBuilder.LogFolder(_context.ProjectFile)}\" mkdir \"{LaunchBuilder.LogFolder(_context.ProjectFile)}\"");
            script.AppendLine();

            var previousWasServer = false;

            foreach (var launch in InstancesToLaunch())
            {
                if (previousWasServer && !launch.IsServer)
                    script.AppendLine($"timeout /t {(int)GameLauncher.ServerHeadStart.TotalSeconds} /nobreak >nul");

                script.AppendLine($"echo Starting {launch.Label}");
                script.AppendLine($"start \"{launch.Label}\" \"{engine.Editor}\" {LaunchBuilder.ToCommandLine(launch.Arguments)}");

                previousWasServer = launch.IsServer;
            }

            File.WriteAllText(dialog.FileName, script.ToString());

            _output.WriteTool($"Saved {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not save the batch file: {ex.Message}", LogSeverity.Error);
        }
    }

    private void OpenLogFolder()
    {
        var folder = LaunchBuilder.LogFolder(_context.ProjectFile);

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not open {folder}: {ex.Message}", LogSeverity.Warning);
        }
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>Stores an option, then re-validates and rebuilds the preview.</summary>
    private void Set<T>(T value, T current, Action<T> store, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
            return;

        store(value);

        OnPropertyChanged(property);

        Refresh();
    }

    private void Refresh()
    {
        var problems = new List<string>();

        if (_context.HasProject && _context.Engine is null)
        {
            problems.Add(string.IsNullOrEmpty(_context.EngineError)
                ? "No Unreal Engine installation resolved."
                : _context.EngineError);
        }

        problems.AddRange(LaunchBuilder.Validate(_context.ProjectFile, _context.Engine, Options));

        ValidationMessage = string.Join("  ·  ", problems);

        CommandPreview = _context is { HasProject: true, Engine: { } engine }
            ? string.Join(
                Environment.NewLine,
                InstancesToLaunch().Select(i => $"REM {i.Label}{Environment.NewLine}\"{engine.Editor}\" {LaunchBuilder.ToCommandLine(i.Arguments)}"))
            : "";

        RaiseRunningState();
    }

    private void RaiseRunningState()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(StatusText));

        (LaunchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (OpenLogFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
