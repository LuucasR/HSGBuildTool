using System;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

public class PackageViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectFile;
    private readonly OutputService _output;

    public PackageViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        var maps = Path.Combine(_root, "Content", "Maps");

        Directory.CreateDirectory(maps);

        _projectFile = Path.Combine(_root, "FMFC.uproject");

        File.WriteAllText(_projectFile, "{ \"EngineAssociation\": \"5.4\" }");

        foreach (var name in new[] { "L_Arena", "L_Hub" })
            File.WriteAllText(Path.Combine(maps, name + ".umap"), "");

        _output = new OutputService(Path.Combine(_root, "Logs"));
    }

    private PackageViewModel Create(AppConfig config, BuildContext context)
        => new(context, new ProcessRunner(), _output, config);

    /// <summary>
    /// Mirrors what MainViewModel does when a project is opened: the context changes
    /// first (which triggers a refresh) and the page reloads afterwards.
    /// </summary>
    [Fact]
    public void Opening_a_project_keeps_the_preset_default_culture()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var context = new BuildContext();
            var page = Create(config, context);

            context.ProjectFile = _projectFile;

            await page.OnProjectChangedAsync();

            var enabled = page.Cultures.Where(c => c.Selected).Select(c => c.Code).ToList();

            Assert.Equal(new[] { "en" }, enabled);
            Assert.Equal(new[] { "en" }, config.GetOrCreate(_projectFile).GetActivePreset().CookCultures);
        });
    }

    [Fact]
    public void Saved_preset_values_are_restored()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var settings = config.GetOrCreate(_projectFile);
            var preset = settings.GetActivePreset();

            preset.Configuration = "Development";
            preset.Platform = "Linux";
            preset.CookCultures = new() { "es", "pt-BR" };
            preset.UseProjectDefaultMaps = false;
            preset.Maps = new() { "/Game/Maps/L_Hub" };
            preset.IoStore = false;

            var context = new BuildContext();
            var page = Create(config, context);

            context.ProjectFile = _projectFile;

            await page.OnProjectChangedAsync();

            Assert.Equal("Development", page.Configuration);
            Assert.Equal("Linux", page.Platform);
            Assert.False(page.IoStore);
            Assert.Equal(new[] { "es", "pt-BR" }, page.Cultures.Where(c => c.Selected).Select(c => c.Code));
            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, page.MapSelection.SelectedMaps);
        });
    }

    /// <summary>
    /// A preset switch must not leak the previous preset's values, and must not write
    /// the previous preset's state over the new one.
    /// </summary>
    [Fact]
    public void Switching_presets_swaps_the_whole_option_set()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var settings = config.GetOrCreate(_projectFile);

            settings.GetActivePreset().Configuration = "Shipping";

            var dev = new BuildPreset
            {
                Name = "Dev",
                Configuration = "Development",
                CookCultures = new() { "es" },
                UseProjectDefaultMaps = false,
                Maps = new() { "/Game/Maps/L_Arena" }
            };

            settings.Presets.Add(dev);

            var context = new BuildContext();
            var page = Create(config, context);

            context.ProjectFile = _projectFile;

            await page.OnProjectChangedAsync();

            Assert.Equal("Shipping", page.Configuration);

            page.SelectedPreset = page.Presets.Single(p => p.Name == "Dev");

            Assert.Equal("Development", page.Configuration);
            Assert.Equal(new[] { "es" }, page.Cultures.Where(c => c.Selected).Select(c => c.Code));
            Assert.Equal(new[] { "/Game/Maps/L_Arena" }, page.MapSelection.SelectedMaps);

            // The original preset must be untouched by the visit to Dev.
            Assert.Equal("Shipping", settings.Presets.Single(p => p.Name == BuildPreset.DefaultName).Configuration);
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
