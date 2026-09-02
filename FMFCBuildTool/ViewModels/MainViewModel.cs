using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
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
    private readonly NotificationService _notifications;
    private readonly ElapsedTimer _sessionElapsed = new();

    private bool _isSessionRunning;
    private BuildOutcome? _lastOutcome;
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

        History = new BuildHistoryService(config);

        _notifications = new NotificationService(config);

        LogViewModel = new OutputViewModel(output, config);

        Package = new PackageViewModel(context, runner, output, config, History);
        Navigation = new NavigationViewModel(context, runner, output, config, History);
        Lighting = new LightingViewModel(context, runner, output, config, History);
        Settings = new SettingsViewModel(config, configService, context, output, ResolveEngine);

        HistoryPage = new HistoryViewModel(History, context, output);

        Queue = new BuildQueueViewModel(
            config,
            output,
            runner,
            context,
            new IBuildPage[] { Package, Navigation, Lighting });

        BrowseProjectCommand = new RelayCommand(BrowseProject);
        ShowPageCommand = new RelayCommand(p => CurrentPageKey = p?.ToString() ?? "Package");
        StopCommand = new RelayCommand(StopEverything, () => IsSessionRunning || _runner.IsRunning);

        // A finished build is worth knowing about even when you have alt-tabbed away, and
        // it is the moment the taskbar button should stop pulsing and go red or clear.
        History.Recorded += record =>
        {
            _lastOutcome = record.Outcome;

            RaiseTaskbarState();

            _notifications.Notify(record.Outcome);
        };

        // One subscription, one banner. The old shell registered three ProcessExited
        // handlers and printed the same "PROCESS EXITED" line several times per build.
        _runner.RunningChanged += () =>
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(BusyDescription));
            OnPropertyChanged(nameof(StatusSummary));

            (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        };

        // The status bar times a *logical* build, which is what a session is. Timing off
        // RunningChanged instead would restart the clock on every map of a nav build,
        // since each map is its own process.
        Output.SessionStarted += () =>
        {
            _sessionElapsed.Restart();

            // A new build clears the red taskbar button from the last failed one.
            _lastOutcome = null;

            IsSessionRunning = true;
        };

        Output.SessionEnded += () =>
        {
            _sessionElapsed.Stop();

            IsSessionRunning = false;
        };

        _sessionElapsed.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SessionElapsedText));

        // The taskbar bar has to follow the running page's own progress, which only the
        // page knows how far along it is.
        foreach (var page in BuildPages.OfType<ObservableObject>())
        {
            page.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(IBuildPage.Progress) or nameof(IBuildPage.IsRunning))
                    RaiseTaskbarState();
            };
        }

        foreach (var project in config.RecentProjects)
            RecentProjects.Add(project);

        _logDockRow = new GridLength(config.LogDockHeight);
    }

    public AppConfig Config { get; }

    public BuildContext Context { get; }

    public OutputService Output { get; }

    public OutputViewModel LogViewModel { get; }

    public BuildHistoryService History { get; }

    public PackageViewModel Package { get; }
    public NavigationViewModel Navigation { get; }
    public LightingViewModel Lighting { get; }
    public BuildQueueViewModel Queue { get; }
    public HistoryViewModel HistoryPage { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<string> RecentProjects { get; } = new();

    public ICommand BrowseProjectCommand { get; }
    public ICommand ShowPageCommand { get; }

    /// <summary>Stops whatever is building, from any page.</summary>
    public ICommand StopCommand { get; }

    public bool IsBusy => _runner.IsRunning;

    public string BusyDescription => _runner.CurrentDescription;

    /// <summary>
    /// True for the whole of one logical build, including the gaps between a navigation
    /// build's per-map processes, when <see cref="IsBusy"/> momentarily goes false.
    /// </summary>
    public bool IsSessionRunning
    {
        get => _isSessionRunning;
        private set
        {
            if (!SetProperty(ref _isSessionRunning, value))
                return;

            OnPropertyChanged(nameof(StatusSummary));

            RaiseTaskbarState();

            (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void RaiseTaskbarState()
    {
        OnPropertyChanged(nameof(TaskbarProgressState));
        OnPropertyChanged(nameof(TaskbarProgressValue));
    }

    public string SessionElapsedText => _sessionElapsed.Text;

    /// <summary>
    /// Drives the Windows taskbar button, so a twenty-minute cook can be watched from the
    /// taskbar instead of by keeping the window on screen. Indeterminate for Package,
    /// which has no readable progress; red for a build that failed.
    /// </summary>
    public TaskbarItemProgressState TaskbarProgressState
    {
        get
        {
            if (IsSessionRunning)
            {
                return RunningPage is { IsProgressIndeterminate: false }
                    ? TaskbarItemProgressState.Normal
                    : TaskbarItemProgressState.Indeterminate;
            }

            return _lastOutcome switch
            {
                BuildOutcome.Failed => TaskbarItemProgressState.Error,
                BuildOutcome.Stopped => TaskbarItemProgressState.Paused,
                _ => TaskbarItemProgressState.None
            };
        }
    }

    /// <summary>0-1, as the taskbar wants it.</summary>
    public double TaskbarProgressValue =>
        RunningPage is { IsProgressIndeterminate: false } page ? page.Progress / 100.0 : 0;

    private IBuildPage? RunningPage =>
        BuildPages.FirstOrDefault(p => p.IsRunning);

    private IBuildPage[] BuildPages => new IBuildPage[] { Package, Navigation, Lighting };

    /// <summary>What the status bar says on the left.</summary>
    public string StatusSummary
    {
        get
        {
            if (!IsSessionRunning)
                return Context.HasProject ? "Idle" : "No project loaded";

            var description = _runner.CurrentDescription;

            return string.IsNullOrWhiteSpace(description) ? "Working" : description;
        }
    }

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
                "Queue" => Queue,
                "Output" => LogViewModel,
                "History" => HistoryPage,
                "Settings" => Settings,
                _ => Package
            };

            if (value == "Settings")
                Settings.RefreshResolved();

            if (value == "History")
                HistoryPage.Refresh();

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

        OnPropertyChanged(nameof(StatusSummary));

        Output.WriteTool($"Opened {Context.ProjectName} ({projectFile})");
    }

    /// <summary>
    /// Cancels whichever page owns the run before killing the process, so a navigation
    /// build does not simply proceed to its next map. Which page it is does not matter —
    /// only one can be running at a time, and asking all three is cheaper than tracking it.
    /// </summary>
    private void StopEverything()
    {
        // Also stops the queue, which watches for a Stopped step and abandons the rest.
        foreach (var page in BuildPages)
        {
            if (page.IsRunning && page.StopCommand.CanExecute(null))
                page.StopCommand.Execute(null);
        }

        _runner.Cancel();
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
