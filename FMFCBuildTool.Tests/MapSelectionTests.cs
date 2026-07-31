using System;
using System.IO;
using System.Linq;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

public class MapSelectionTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectFile;

    public MapSelectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        var maps = Path.Combine(_root, "Content", "Maps");

        Directory.CreateDirectory(maps);

        _projectFile = Path.Combine(_root, "FMFC.uproject");

        File.WriteAllText(_projectFile, "{ \"EngineAssociation\": \"5.4\" }");

        foreach (var name in new[] { "L_Arena", "L_Hub", "L_Warning", "L_Test" })
            File.WriteAllText(Path.Combine(maps, name + ".umap"), "");
    }

    [Fact]
    public void Scanner_produces_game_package_paths()
    {
        var maps = MapScanner.Scan(_projectFile);

        Assert.Equal(4, maps.Count);
        Assert.Contains(maps, m => m.RelativePath == "/Game/Maps/L_Arena");
        Assert.All(maps, m => Assert.DoesNotContain(".umap", m.RelativePath));
    }

    /// <summary>
    /// The worst bug in the old tool: the build read its map list back from the ListView's
    /// Items, which the search box had replaced with a filtered copy. Typing in the search
    /// box and pressing BUILD silently dropped every selected map that didn't match.
    /// </summary>
    [Fact]
    public void Searching_does_not_change_which_maps_are_selected()
    {
        Sta.Run(async () =>
        {
            var selection = new MapSelectionViewModel();

            await selection.LoadAsync(_projectFile);

            selection.SelectAllCommand.Execute(null);

            Assert.Equal(4, selection.SelectedMaps.Count);

            selection.Search = "arena";

            // The view is filtered down to one row...
            Assert.Single(selection.Maps.Cast<object>());

            // ...but the build still sees all four.
            Assert.Equal(4, selection.SelectedMaps.Count);
            Assert.Contains("/Game/Maps/L_Hub", selection.SelectedMaps);
        });
    }

    [Fact]
    public void Select_all_applies_to_the_filtered_view_only()
    {
        Sta.Run(async () =>
        {
            var selection = new MapSelectionViewModel();

            await selection.LoadAsync(_projectFile);

            selection.SelectNoneCommand.Execute(null);

            selection.Search = "arena";
            selection.SelectAllCommand.Execute(null);

            selection.Search = "";

            Assert.Equal(new[] { "/Game/Maps/L_Arena" }, selection.SelectedMaps);
        });
    }

    [Fact]
    public void Saved_selection_is_restored_on_load()
    {
        Sta.Run(async () =>
        {
            var selection = new MapSelectionViewModel();

            await selection.LoadAsync(_projectFile, new[] { "/Game/Maps/L_Hub" });

            Assert.Equal("1 of 4 selected", selection.SelectionSummary);
            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, selection.SelectedMaps);
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
