using System.Collections.Generic;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The generated Python, and — the point of this file — that it still says what the
/// collector still reads.
/// </summary>
public class BlueprintScriptWriterTests
{
    private static readonly string[] Paths =
    {
        "/Game/BP/BP_Door",
        "/Game/BP/BP_Chest",
        "/Game/Characters/BP_Hero"
    };

    [Fact]
    public void The_script_names_every_blueprint_it_was_given()
    {
        var script = BlueprintScriptWriter.ForPaths(Paths, save: true);

        foreach (var path in Paths)
            Assert.Contains($"\"{path}\"", script);
    }

    [Fact]
    public void The_script_refreshes_through_compile_blueprint()
    {
        var script = BlueprintScriptWriter.ForPaths(Paths, save: true);

        Assert.Contains("unreal.BlueprintEditorLibrary.compile_blueprint(asset)", script);
    }

    /// <summary>One bad asset must not cost the answers for the rest of the list.</summary>
    [Fact]
    public void One_failing_asset_does_not_abort_the_loop()
    {
        var script = BlueprintScriptWriter.ForPaths(Paths, save: true);

        Assert.Contains("except Exception as error:", script);
    }

    [Fact]
    public void Saving_is_what_the_save_flag_decides()
    {
        Assert.Contains("save_loaded_asset", BlueprintScriptWriter.ForPaths(Paths, save: true));
        Assert.DoesNotContain("save_loaded_asset", BlueprintScriptWriter.ForPaths(Paths, save: false));
    }

    /// <summary>
    /// Package paths do not normally contain a quote or a backslash, which is exactly why
    /// an unescaped one would break the whole script over a single unlucky asset.
    /// </summary>
    [Fact]
    public void Path_literals_are_escaped()
    {
        var script = BlueprintScriptWriter.ForPaths(new[] { "/Game/Odd\"Name" }, save: true);

        Assert.Contains("\"/Game/Odd\\\"Name\"", script);
    }

    /// <summary>
    /// The .bat export cannot know what failed, so it discovers the list at run time —
    /// and only under /Game, because engine content is not ours to save.
    /// </summary>
    [Fact]
    public void The_export_variant_discovers_its_own_list_under_game()
    {
        var script = BlueprintScriptWriter.ForAllBlueprints(save: true);

        Assert.Contains("PATHS = _all_blueprint_paths()", script);
        Assert.Contains("name.startswith(\"/Game/\")", script);
        Assert.DoesNotContain("/Game/BP/BP_Door", script);
    }

    // ---------------------------------------------------------------- round trip

    /// <summary>
    /// The test that stops the generator and the parser drifting apart: the log the
    /// script's own markers would produce, fed through the real collector.
    /// </summary>
    [Fact]
    public void The_log_the_script_would_print_is_the_log_the_collector_reads()
    {
        var script = BlueprintScriptWriter.ForPaths(Paths, save: true);

        // The script prints "%s %s" % (BEGIN, path), and BEGIN is whatever it declared.
        Assert.Contains($"BEGIN = \"{BlueprintBuilder.BeginMarker}\"", script);
        Assert.Contains($"END = \"{BlueprintBuilder.EndMarker}\"", script);
        Assert.Contains($"SUMMARY = \"{BlueprintBuilder.SummaryMarker}\"", script);
        Assert.Contains("unreal.log(\"%s %s\" % (BEGIN, path))", script);
        Assert.Contains("unreal.log(\"%s %s %s\" % (END, path, result))", script);

        var collector = new BlueprintFailureCollector(BlueprintPassKind.Refresh);

        foreach (var line in Transcript(Paths))
            collector.Observe(LogParser.Parse(line));

        var report = collector.Complete(0);

        Assert.True(report.Conclusive);
        Assert.Equal(Paths.Length, report.Results.Count);
        Assert.Equal(Paths, report.Results.Select(row => row.Path).ToArray());
        Assert.All(report.Results, row => Assert.Equal(BlueprintRunState.Reloaded, row.State));
    }

    /// <summary>What the editor's log looks like once unreal.log has wrapped the markers.</summary>
    private static IEnumerable<string> Transcript(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            yield return $"LogPython: Display: {BlueprintBuilder.BeginMarker} {path}";
            yield return $"LogPython: Display: {BlueprintBuilder.EndMarker} {path} {BlueprintBuilder.ResultOk}";
        }

        yield return $"LogPython: Display: {BlueprintBuilder.SummaryMarker} attempted={paths.Count} saved={paths.Count}";
    }
}
