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
    /// </summary>
    [Fact]
    public void Saved_navigation_selection_is_restored()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();

            config.GetOrCreate(_projectFile).NavigationMaps = new() { "/Game/Maps/L_Hub" };

            var context = new BuildContext();
            var page = new NavigationViewModel(context, new ProcessRunner(), _output, config);

            context.ProjectFile = _projectFile;

            await page.OnProjectChangedAsync();

            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, page.MapSelection.SelectedMaps);
            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, config.GetOrCreate(_projectFile).NavigationMaps);
        });
    }

    [Fact]
    public void Changing_the_selection_writes_it_back()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var context = new BuildContext();
            var page = new NavigationViewModel(context, new ProcessRunner(), _output, config);

            context.ProjectFile = _projectFile;

            await page.OnProjectChangedAsync();

            page.MapSelection.SelectAllCommand.Execute(null);

            Assert.Equal(3, config.GetOrCreate(_projectFile).NavigationMaps.Count);
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

            var nav = new NavigationViewModel(context, runner, _output, config);
            var lighting = new LightingViewModel(context, runner, _output, config);

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
            var page = new LightingViewModel(context, new ProcessRunner(), _output, config);

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
