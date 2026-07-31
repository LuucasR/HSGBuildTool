using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// The shell: owns the shared services, the page view-models, and the current project.
/// </summary>
/// <remarks>
/// Page view-models are created once and kept. The old shell built a brand-new
/// UserControl on every rail click, so search text, scroll position and any unsaved
/// checkbox change were discarded whenever you looked at another tab.
/// </remarks>
public sealed class MainViewModel : ObservableObject
{
    private readonly ConfigService _configService;
    private readonly ProcessRunner _runner;

    private object? _currentPage;
    private string _currentPageKey = "";
    private string _selectedProject = "";
    private bool _suspendProjectChange;
    private GridLength _logDockRow;

    public MainViewModel(
        AppConfig config,
        ConfigService configService,
        OutputService output,
        ProcessRunner runner,
        BuildContext context)
    {
        Config = config;
        _configService = configService;
        Output = output;
        _runner = runner;
        Context = context;

        LogViewModel = new OutputViewModel(output) { LogFolderFallback = configService.LogDirectory };

        Package = new PackageViewModel(context, runner, output, config);
        Navigation = new NavigationViewModel(context, runner, output, config);
        Lighting = new LightingViewModel(context, runner, output, config);
        Settings = new SettingsViewModel(config, configService, context, output, ResolveEngine);

        BrowseProjectCommand = new RelayCommand(BrowseProject);
        ShowPageCommand = new RelayCommand(p => CurrentPageKey = p?.ToString() ?? "Package");

        // One subscription, one banner. The old shell registered three ProcessExited
        // handlers and printed the same "PROCESS EXITED" line several times per build.
        _runner.RunningChanged += () =>
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyDescription));
        };

        foreach (var project in config.RecentProjects)
            RecentProjects.Add(project);

        _logDockRow = new GridLength(config.LogDockHeight);
    }

    public AppConfig Config { get; }

    public BuildContext Context { get; }

    public OutputService Output { get; }

    public OutputViewModel LogViewModel { get; }

    public PackageViewModel Package { get; }
    public NavigationViewModel Navigation { get; }
    public LightingViewModel Lighting { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<string> RecentProjects { get; } = new();

    public ICommand BrowseProjectCommand { get; }
    public ICommand ShowPageCommand { get; }

    public bool IsBusy => _runner.IsRunning;

    public string BusyDescription => _runner.CurrentDescription;

    /// <summary>
    /// Height of the bottom log dock, bound two-way to the grid row so dragging the
    /// splitter persists. Collapses to zero on the full-screen Output page.
    /// </summary>
    public GridLength LogDockRow
    {
        get => _logDockRow;
        set
        {
            if (!SetProperty(ref _logDockRow, value))
                return;

            if (value.IsAbsolute && value.Value > 40)
                Config.LogDockHeight = value.Value;
        }
    }

    public bool IsLogDockVisible => CurrentPageKey != "Output";

    public string CurrentPageKey
    {
        get => _currentPageKey;
        set
        {
            if (!SetProperty(ref _currentPageKey, value))
                return;

            Config.LastPage = value;

            CurrentPage = value switch
            {
                "Navigation" => Navigation,
                "Lighting" => Lighting,
                "Output" => LogViewModel,
                "Settings" => Settings,
                _ => Package
            };

            if (value == "Settings")
                Settings.RefreshResolved();

            LogDockRow = value == "Output"
                ? new GridLength(0)
                : new GridLength(Config.LogDockHeight);

            OnPropertyChanged(nameof(IsLogDockVisible));
        }
    }

    public object? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value) || _suspendProjectChange)
                return;

            _ = OpenProjectAsync(value);
        }
    }

    public async Task InitializeAsync()
    {
        Output.WriteTool($"FMFC Build Tool — settings at {_configService.ConfigPath}");

        if (!string.IsNullOrWhiteSpace(Config.LastProject) && ProjectLoader.IsValidProject(Config.LastProject))
            await OpenProjectAsync(Config.LastProject);
        else if (!string.IsNullOrWhiteSpace(Config.LastProject))
            Output.WriteTool($"The last project is no longer at {Config.LastProject}.", LogSeverity.Warning);

        CurrentPageKey = string.IsNullOrWhiteSpace(Config.LastPage) ? "Package" : Config.LastPage;
    }

    public async Task OpenProjectAsync(string projectFile)
    {
        if (string.IsNullOrWhiteSpace(projectFile))
            return;

        if (!ProjectLoader.IsValidProject(projectFile))
        {
            Output.WriteTool($"Not a valid .uproject: {projectFile}", LogSeverity.Error);
            return;
        }

        Context.ProjectFile = projectFile;
        Config.LastProject = projectFile;

        Config.TouchRecent(projectFile);
        SyncRecentProjects();

        ResolveEngine();

        // Every page reloads from the same context, so Package and Navigation can no
        // longer disagree about which project or engine is in play.
        await Package.OnProjectChangedAsync();
        await Navigation.OnProjectChangedAsync();
        await Lighting.OnProjectChangedAsync();

        Settings.RefreshResolved();

        Output.WriteTool($"Opened {Context.ProjectName} ({projectFile})");
    }

    private void ResolveEngine()
    {
        if (UnrealLocator.TryResolve(Context.ProjectFile, Config.EnginePathOverride, out var engine, out var error))
        {
            Context.Engine = engine;
            Context.EngineError = "";

            Output.WriteTool($"Using Unreal Engine {engine.Version} at {engine.Root} ({engine.Source}).");
        }
        else
        {
            Context.Engine = null;
            Context.EngineError = error;

            Output.WriteTool(error, LogSeverity.Error);
        }

        Settings.RefreshResolved();
    }

    private void BrowseProject()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Unreal Project (*.uproject)|*.uproject",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
            _ = OpenProjectAsync(dialog.FileName);
    }

    private void SyncRecentProjects()
    {
        _suspendProjectChange = true;

        try
        {
            RecentProjects.Clear();

            foreach (var project in Config.RecentProjects)
                RecentProjects.Add(project);

            _selectedProject = Context.ProjectFile;

            OnPropertyChanged(nameof(SelectedProject));
        }
        finally
        {
            _suspendProjectChange = false;
        }
    }

    /// <summary>
    /// Persists everything on exit. The old tool only saved after a build completed, so
    /// closing the app — or cancelling — threw away every option change since launch.
    /// </summary>
    public void Save()
    {
        _configService.Save(Config);
    }

    public void Shutdown()
    {
        _runner.Cancel();

        Save();

        Output.Dispose();
    }
}
