using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Ookii.Dialogs.Wpf;

namespace FMFCBuildTool.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private readonly BuildContext _context;
    private readonly OutputService _output;
    private readonly Action _reresolveEngine;

    private bool _isScanning;

    public SettingsViewModel(
        AppConfig config,
        ConfigService configService,
        BuildContext context,
        OutputService output,
        Action reresolveEngine)
    {
        _config = config;
        _configService = configService;
        _context = context;
        _output = output;
        _reresolveEngine = reresolveEngine;

        BrowseEngineCommand = new RelayCommand(BrowseEngine);
        ClearEngineOverrideCommand = new RelayCommand(ClearEngineOverride);
        UseEngineCommand = new RelayCommand(p => UseEngine(p as EnginePaths));
        RescanEnginesCommand = new RelayCommand(RescanEngines);
        BrowseArchiveRootCommand = new RelayCommand(BrowseArchiveRoot);
        RevealConfigCommand = new RelayCommand(() => Reveal(_configService.ConfigDirectory));
        RevealLogsCommand = new RelayCommand(() => Reveal(_configService.LogDirectory));

        RescanEngines();
    }

    public ObservableCollection<EnginePaths> DetectedEngines { get; } = new();

    public ICommand BrowseEngineCommand { get; }
    public ICommand ClearEngineOverrideCommand { get; }
    public ICommand UseEngineCommand { get; }
    public ICommand RescanEnginesCommand { get; }
    public ICommand BrowseArchiveRootCommand { get; }
    public ICommand RevealConfigCommand { get; }
    public ICommand RevealLogsCommand { get; }

    public string ConfigPath => _configService.ConfigPath;

    public string LogDirectory => _configService.LogDirectory;

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetProperty(ref _isScanning, value);
    }

    /// <summary>
    /// Manual engine root. Empty means "detect from the .uproject", which is the normal case —
    /// the old tool required this to be browsed by hand before Navigation would work at all.
    /// </summary>
    public string EnginePathOverride
    {
        get => _config.EnginePathOverride;
        set
        {
            if (_config.EnginePathOverride == value)
                return;

            _config.EnginePathOverride = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOverrideActive));

            _reresolveEngine();
        }
    }

    public bool IsOverrideActive => !string.IsNullOrWhiteSpace(_config.EnginePathOverride);

    public string DefaultArchiveRoot
    {
        get => _config.DefaultArchiveRoot;
        set
        {
            if (_config.DefaultArchiveRoot == value)
                return;

            _config.DefaultArchiveRoot = value;

            OnPropertyChanged();
        }
    }

    public IReadOnlyList<int> RetentionOptions { get; } = new[] { 3, 7, 14, 30, 90 };

    public int LogRetentionDays
    {
        get => _config.LogRetentionDays;
        set
        {
            if (_config.LogRetentionDays == value)
                return;

            _config.LogRetentionDays = value;

            OnPropertyChanged();
        }
    }

    public string ResolvedEngineSummary => _context.Engine is { } engine
        ? $"UE {engine.Version} · {engine.Source} · {(engine.IsInstalled ? "binary" : "source")} build"
        : string.IsNullOrEmpty(_context.EngineError) ? "Not resolved" : _context.EngineError;

    public void RefreshResolved() => OnPropertyChanged(nameof(ResolvedEngineSummary));

    private void RescanEngines()
    {
        IsScanning = true;

        try
        {
            DetectedEngines.Clear();

            foreach (var engine in UnrealLocator.DiscoverInstalled())
                DetectedEngines.Add(engine);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void UseEngine(EnginePaths? engine)
    {
        if (engine is not null)
            EnginePathOverride = engine.Root;
    }

    private void BrowseEngine()
    {
        var dialog = new VistaFolderBrowserDialog
        {
            Description = "Select the Unreal Engine root folder (the one containing Engine\\).",
            UseDescriptionForTitle = true,
            SelectedPath = EnginePathOverride
        };

        if (dialog.ShowDialog(Application.Current.MainWindow) != true)
            return;

        var selected = dialog.SelectedPath;

        // Be forgiving about which level of the tree was picked: walk up until an
        // engine root is found rather than only special-casing Engine\Binaries\Win64.
        var directory = new DirectoryInfo(selected);

        while (directory is not null)
        {
            if (UnrealLocator.FromRoot(directory.FullName, "", "Manual override") is not null)
            {
                EnginePathOverride = directory.FullName;
                return;
            }

            directory = directory.Parent;
        }

        _output.WriteTool(
            $"{selected} does not look like an Unreal Engine root (no Engine\\Build\\BatchFiles\\RunUAT.bat below it).",
            LogSeverity.Warning);
    }

    private void ClearEngineOverride() => EnginePathOverride = "";

    private void BrowseArchiveRoot()
    {
        var dialog = new VistaFolderBrowserDialog { SelectedPath = DefaultArchiveRoot };

        if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            DefaultArchiveRoot = dialog.SelectedPath;
    }

    private void Reveal(string folder)
    {
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
}
