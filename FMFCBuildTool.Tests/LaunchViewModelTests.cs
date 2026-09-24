using System;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The Launch page's map search. Projects here have hundreds of maps, so the search is how
/// a map gets picked at all — it has to narrow well and never lose the current choice.
/// </summary>
public class LaunchViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly BuildContext _context;
    private readonly OutputService _output;

    public LaunchViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        var projectFile = Path.Combine(_root, "FMFC.uproject");

        foreach (var map in new[] { @"Maps\Arena\L_Arena_Day", @"Maps\Arena\L_Arena_Night", @"Maps\Forest\L_Forest_Night", @"Dev\L_Test" })
        {
            var file = Path.Combine(_root, "Content", map + ".umap");

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "");
        }

        File.WriteAllText(projectFile, "{ \"EngineAssociation\": \"5.4\" }");

        _output = new OutputService(Path.Combine(_root, "Logs"));
        _context = new BuildContext { ProjectFile = projectFile };
    }

    [Fact]
    public void Every_word_of_the_search_must_match_in_any_order()
    {
        Sta.Run(async () =>
        {
            var page = Create();

            await page.OnProjectChangedAsync();

            Assert.Equal("4 maps", page.MapSummary);

            page.MapSearch = "night arena";

            Assert.Equal(
                new[] { LaunchViewModel.DefaultMapEntry, "/Game/Maps/Arena/L_Arena_Night" },
                page.FilteredMaps.Cast<string>());

            Assert.Equal("1 of 4 maps", page.MapSummary);
            Assert.Equal("/Game/Maps/Arena/L_Arena_Night", page.FirstSearchMatch);
        });
    }

    [Fact]
    public void The_selected_map_stays_listed_when_the_search_does_not_match_it()
    {
        Sta.Run(async () =>
        {
            var page = Create();

            await page.OnProjectChangedAsync();

            page.Map = "/Game/Dev/L_Test";
            page.MapSearch = "forest";

            var shown = page.FilteredMaps.Cast<string>().ToList();

            Assert.Contains("/Game/Dev/L_Test", shown);
            Assert.Contains("/Game/Maps/Forest/L_Forest_Night", shown);
            Assert.Equal("/Game/Maps/Forest/L_Forest_Night", page.FirstSearchMatch);
            Assert.Equal("/Game/Dev/L_Test", page.Map);
        });
    }

    private LaunchViewModel Create()
        => new(_context, new GameLauncher(_output), _output, new AppConfig());

    public void Dispose()
    {
        _output.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
