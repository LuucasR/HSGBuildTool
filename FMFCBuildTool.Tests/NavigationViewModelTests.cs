using System;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

public class NavigationViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectFile;
    private readonly OutputService _output;

    public NavigationViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        var maps = Path.Combine(_root, "Content", "Maps");

        Directory.CreateDirectory(maps);

        _projectFile = Path.Combine(_root, "FMFC.uproject");

        File.WriteAllText(_projectFile, "{ \"EngineAssociation\": \"5.4\" }");

        foreach (var name in new[] { "L_Arena", "L_Hub", "L_Test" })
            File.WriteAllText(Path.Combine(maps, name + ".umap"), "");

        _output = new OutputService(Path.Combine(_root, "Logs"));
    }

    /// <summary>
    /// The saved nav selection must survive the scan that happens when the project opens.
    /// A config written before presets existed keeps its flat NavigationMaps list; it has
    /// to come back as the Default preset rather than being silently dropped.
    /// </summary>
    [Fact]
    public void A_pre_preset_navigation_selection_is_migrated_and_restored()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();

            config.GetOrCreate(_projectFile).NavigationMaps = new() { "/Game/Maps/L_Hub" };

            var context = new BuildContext();
            var page = new NavigationViewModel(context, new ProcessRunner(), _output, config, new BuildHistoryService(config));

            context.ProjectFile = _projectFile;

            await page.OnProjectChangedAsync();

            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, page.MapSelection.SelectedMaps);

            var preset = config.GetOrCreate(_projectFile).GetActiveCommandletPreset("nav");

            Assert.Equal(CommandletPreset.DefaultName, preset.Name);
            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, preset.Maps);
        });
    }

    [Fact]
    public void Changing_the_selection_writes_it_into_the_active_preset()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var context = new BuildContext();
            var page = new NavigationViewModel(context, new ProcessRunner(), _output, config, new BuildHistoryService(config));

            context.ProjectFile = _projectFile;

            await page.OnProjectChangedAsync();

            page.MapSelection.SelectAllCommand.Execute(null);

            Assert.Equal(3, config.GetOrCreate(_projectFile).GetActiveCommandletPreset("nav").Maps.Count);
        });
    }

    /// <summary>
    /// Two presets on the same page keep separate selections, and switching between them
    /// swaps what is checked. This is the whole reason the pages grew presets.
    /// </summary>
    [Fact]
    public void Presets_keep_separate_selections()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var settings = config.GetOrCreate(_projectFile);

            settings.NavigationPresets.Add(new CommandletPreset { Maps = { "/Game/Maps/L_Hub" } });
            settings.NavigationPresets.Add(new CommandletPreset { Name = "Combat", Maps = { "/Game/Maps/L_Arena" } });

            var context = new BuildContext();
            var page = new NavigationViewModel(context, new ProcessRunner(), _output, config, new BuildHistoryService(config));

            context.ProjectFile = _projectFile;

            await page.OnProjectChangedAsync();

            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, page.MapSelection.SelectedMaps);

            page.SelectedPreset = page.Presets.Single(p => p.Name == "Combat");

            Assert.Equal(new[] { "/Game/Maps/L_Arena" }, page.MapSelection.SelectedMaps);

            // Editing under one preset must not reach into the other.
            page.MapSelection.SelectAllCommand.Execute(null);

            Assert.Equal(3, settings.NavigationPresets.Single(p => p.Name == "Combat").Maps.Count);
            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, settings.NavigationPresets[0].Maps);
        });
    }

    /// <summary>Navigation and Lighting keep independent selections for the same project.</summary>
    [Fact]
    public void Navigation_and_lighting_selections_are_independent()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var settings = config.GetOrCreate(_projectFile);

            settings.NavigationMaps = new() { "/Game/Maps/L_Hub" };
            settings.LightingMaps = new() { "/Game/Maps/L_Arena" };

            var context = new BuildContext();
            var runner = new ProcessRunner();

            var nav = new NavigationViewModel(context, runner, _output, config, new BuildHistoryService(config));
            var lighting = new LightingViewModel(context, runner, _output, config, new BuildHistoryService(config));

            context.ProjectFile = _projectFile;

            await nav.OnProjectChangedAsync();
            await lighting.OnProjectChangedAsync();

            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, nav.MapSelection.SelectedMaps);
            Assert.Equal(new[] { "/Game/Maps/L_Arena" }, lighting.MapSelection.SelectedMaps);
        });
    }

    [Fact]
    public void Lighting_restores_the_saved_quality()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();

            config.GetOrCreate(_projectFile).LightingQuality = "Medium";

            var context = new BuildContext();
            var page = new LightingViewModel(context, new ProcessRunner(), _output, config, new BuildHistoryService(config));

            context.ProjectFile = _projectFile;

            await page.OnProjectChangedAsync();

            Assert.Equal("Medium", page.Quality);
        });
    }

    public void Dispose()
    {
        _output.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
