using System;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The Compiler page's own behaviour around the Blueprint options.
/// </summary>
/// <remarks>
/// <see cref="ProcessRunner"/> is concrete and not injectable, so nothing here runs a
/// step. What is worth pinning is what the page shows and remembers before anything runs:
/// the preview has to show every step it is about to take, and the options have to
/// survive closing the tool — the reason the "package builder configuration persistence"
/// work happened in the first place.
/// </remarks>
public class CompilerViewModelTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectFile;
    private readonly OutputService _output;
    private readonly BuildContext _context;

    public CompilerViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        var source = Path.Combine(_root, "Source");
        var engine = Path.Combine(_root, "Engine");

        Directory.CreateDirectory(source);
        Directory.CreateDirectory(engine);

        _projectFile = Path.Combine(_root, "FMFC.uproject");

        File.WriteAllText(_projectFile, "{ \"EngineAssociation\": \"5.4\" }");
        File.WriteAllText(Path.Combine(source, "FMFCEditor.Target.cs"), "Type = TargetType.Editor;");

        var buildBat = Path.Combine(engine, "Build.bat");
        var editorCmd = Path.Combine(engine, "UnrealEditor-Cmd.exe");

        File.WriteAllText(buildBat, "");
        File.WriteAllText(editorCmd, "");

        _output = new OutputService(Path.Combine(_root, "Logs"));

        _context = new BuildContext
        {
            ProjectFile = _projectFile,
            Engine = new EnginePaths
            {
                Root = engine,
                RunUAT = Path.Combine(engine, "RunUAT.bat"),
                EditorCmd = editorCmd,
                BuildBat = buildBat
            }
        };
    }

    [Fact]
    public void The_preview_is_one_line_until_the_blueprint_options_are_ticked()
    {
        Sta.Run(async () =>
        {
            var page = Create(new AppConfig());

            await page.OnProjectChangedAsync();

            Assert.Single(Lines(page.CommandPreview));

            page.UpdateBlueprints = true;

            var withCompile = Lines(page.CommandPreview);

            Assert.Equal(2, withCompile.Length);
            Assert.Contains("-run=CompileAllBlueprints", withCompile[1]);

            page.UpdateAllNodesInBlueprints = true;

            var withRefresh = Lines(page.CommandPreview);

            // The refresh step's script does not exist until the step before it has found
            // something, so it can only ever be shown as a comment.
            Assert.Equal(4, withRefresh.Length);
            Assert.StartsWith("REM", withRefresh[2]);
            Assert.Contains("-run=pythonscript", withRefresh[3]);
        });
    }

    [Fact]
    public void The_blueprint_options_are_remembered_per_project()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var page = Create(config);

            await page.OnProjectChangedAsync();

            page.UpdateBlueprints = true;
            page.UpdateAllNodesInBlueprints = true;
            page.BlueprintExtraArguments = "-DirtyOnly";

            var reopened = Create(config);

            await reopened.OnProjectChangedAsync();

            Assert.True(reopened.UpdateBlueprints);
            Assert.True(reopened.UpdateAllNodesInBlueprints);
            Assert.Equal("-DirtyOnly", reopened.BlueprintExtraArguments);
        });
    }

    /// <summary>Nobody's existing compile should grow an editor start-up by upgrading.</summary>
    [Fact]
    public void Both_options_are_off_for_a_project_that_has_never_seen_them()
    {
        Sta.Run(async () =>
        {
            var page = Create(new AppConfig());

            await page.OnProjectChangedAsync();

            Assert.False(page.UpdateBlueprints);
            Assert.False(page.UpdateAllNodesInBlueprints);
            Assert.False(page.HasBlueprintResults);
        });
    }

    /// <summary>
    /// The second option on its own would look armed and do nothing, since it works from
    /// the first step's failures.
    /// </summary>
    [Fact]
    public void Refreshing_nodes_without_the_step_that_finds_them_blocks_the_run()
    {
        Sta.Run(async () =>
        {
            var page = Create(new AppConfig());

            await page.OnProjectChangedAsync();

            Assert.True(page.CanRun);

            page.UpdateAllNodesInBlueprints = true;

            Assert.False(page.CanRun);
            Assert.Contains("Update all nodes", page.ValidationMessage);
        });
    }

    /// <summary>
    /// Its checkbox is disabled without the step above it, so leaving it ticked would
    /// strand the page on a validation error with no control left to clear it.
    /// </summary>
    [Fact]
    public void Turning_the_first_option_off_turns_the_dependent_one_off_too()
    {
        Sta.Run(async () =>
        {
            var page = Create(new AppConfig());

            await page.OnProjectChangedAsync();

            page.UpdateBlueprints = true;
            page.UpdateAllNodesInBlueprints = true;

            page.UpdateBlueprints = false;

            Assert.False(page.UpdateAllNodesInBlueprints);
            Assert.True(page.CanRun);
        });
    }

    /// <summary>
    /// The one Blueprint default that is not "off", and only because it cannot surprise
    /// anyone: a config written before it existed deserialises to the wider behaviour.
    /// </summary>
    [Fact]
    public void Skipping_engine_content_is_on_by_default_and_shows_in_the_command()
    {
        Sta.Run(async () =>
        {
            var config = new AppConfig();
            var page = Create(config);

            await page.OnProjectChangedAsync();

            Assert.True(page.BlueprintSkipEngineContent);

            page.UpdateBlueprints = true;

            Assert.Contains("-IgnoreFolder=", page.CommandPreview);

            page.BlueprintSkipEngineContent = false;

            Assert.DoesNotContain("-IgnoreFolder=", page.CommandPreview);

            var reopened = Create(config);

            await reopened.OnProjectChangedAsync();

            Assert.False(reopened.BlueprintSkipEngineContent);
        });
    }

    private static string[] Lines(string preview)
        => preview.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private CompilerViewModel Create(AppConfig config)
        => new(_context, new ProcessRunner(), _output, config, new BuildHistoryService(config));

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
