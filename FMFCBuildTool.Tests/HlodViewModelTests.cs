using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The HLOD page's own behaviour: its options belong to the preset, and its preset list
/// is its own rather than a second view of Navigation's.
/// </summary>
public class HlodViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectFile;
    private readonly OutputService _output;

    public HlodViewModelTests()
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

    /// <summary>
    /// Options write straight through to the preset, the way Lighting's quality does.
    /// The old pages only saved after a build finished, so closing the tool threw away
    /// every change since launch.
    /// </summary>
    [Fact]
    public void Options_are_kept_on_the_preset_as_they_change()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var page = Create(config);

            await page.OnProjectChangedAsync();

            page.Builder = HlodBuilder.Unreal;
            page.DeleteHlods = false;
            page.WaitMutex = false;
            page.ExtraArguments = "-DistributedBuild";

            var preset = config.GetOrCreate(_projectFile).GetActiveCommandletPreset("hlod");

            Assert.Equal(HlodBuilder.Unreal, preset.HlodBuilder);
            Assert.False(preset.DeleteHlods);
            Assert.False(preset.WaitMutex);
            Assert.Equal("-DistributedBuild", preset.ExtraArguments);

            // And they come back on the next page built against the same config.
            var reopened = Create(config);

            await reopened.OnProjectChangedAsync();

            Assert.Equal(HlodBuilder.Unreal, reopened.Builder);
            Assert.False(reopened.DeleteHlods);
            Assert.False(reopened.WaitMutex);
            Assert.Equal("-DistributedBuild", reopened.ExtraArguments);
        });
    }

    /// <summary>
    /// HLOD keeps its own preset list. PresetsFor used to fall through to Navigation's for
    /// anything that was not "lighting", which would have made the two pages share maps.
    /// </summary>
    [Fact]
    public void The_preset_list_is_not_shared_with_navigation()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();

            var hlod = Create(config);
            var nav = new NavigationViewModel(
                Context(), new ProcessRunner(), _output, config, new BuildHistoryService(config));

            await hlod.OnProjectChangedAsync();
            await nav.OnProjectChangedAsync();

            hlod.MapSelection.ApplySelection(new[] { "/Game/Maps/L_Arena" });
            nav.MapSelection.ApplySelection(new[] { "/Game/Maps/L_Hub" });

            var settings = config.GetOrCreate(_projectFile);

            Assert.Equal(new[] { "/Game/Maps/L_Arena" }, settings.GetActiveCommandletPreset("hlod").Maps);
            Assert.Equal(new[] { "/Game/Maps/L_Hub" }, settings.GetActiveCommandletPreset("nav").Maps);
        });
    }

    /// <summary>The preview is the two commands you would otherwise type by hand.</summary>
    [Fact]
    public void The_preview_shows_the_delete_and_build_invocations()
    {
        Sta.Run(async () =>
        {
            var page = Create(new AppConfig());

            await page.OnProjectChangedAsync();

            page.MapSelection.ApplySelection(new[] { "/Game/Maps/L_Arena" });

            var lines = page.CommandPreview.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, lines.Length);
            Assert.Contains("-DeleteHLODs", lines[0]);
            Assert.Contains("-SetupHLODs -BuildHLODs", lines[1]);
            Assert.All(lines, l => Assert.Contains($"-Builder={HlodBuilder.Simplygon}", l));
        });
    }

    /// <summary>Run stays dead while the options describe a run that would do nothing.</summary>
    [Fact]
    public void Nothing_to_do_blocks_the_run()
    {
        Sta.Run(async () =>
        {
            var page = Create(new AppConfig());

            await page.OnProjectChangedAsync();

            page.MapSelection.ApplySelection(new[] { "/Game/Maps/L_Arena" });

            Assert.True(page.CanRun);

            page.DeleteHlods = false;
            page.SetupHlods = false;
            page.BuildHlods = false;

            Assert.False(page.CanRun);
            Assert.Contains("Nothing to do", page.ValidationMessage);
        });
    }

    private BuildContext Context()
    {
        var context = new BuildContext { ProjectFile = _projectFile };

        context.Engine = new EnginePaths
        {
            Root = _root,
            RunUAT = "unused",
            BuildBat = "unused",
            EditorCmd = Path.Combine(Environment.SystemDirectory, "cmd.exe")
        };

        return context;
    }

    private HlodViewModel Create(AppConfig config)
        => new(Context(), new ProcessRunner(), _output, config, new BuildHistoryService(config));

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
