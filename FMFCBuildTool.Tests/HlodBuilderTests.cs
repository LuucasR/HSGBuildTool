using System;
using System.Collections.Generic;
using System.Linq;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The HLOD command lines. The pair of invocations is the whole point: passing
/// -DeleteHLODs alongside -BuildHLODs deletes the HLODs and then has nothing to build,
/// which is why deleting is its own pass rather than another flag on one command.
/// </summary>
public class HlodBuilderTests
{
    private const string ProjectFile = @"D:\Proj\FMFC.uproject";
    private const string Map = "/Game/MelianMap/L_Melian";

    private static HlodOptions Options(
        string builder = HlodBuilder.Simplygon,
        bool delete = true,
        bool setup = true,
        bool build = true,
        bool rendering = true,
        bool waitMutex = true,
        bool unattended = true,
        string extra = "")
        => new(builder, delete, setup, build, rendering, waitMutex, unattended, extra);

    [Fact]
    public void A_full_run_is_a_delete_pass_followed_by_a_build_pass()
    {
        var passes = HlodBuilder.Passes(ProjectFile, Map, Options());

        Assert.Equal(2, passes.Count);

        Assert.Equal("Delete HLODs", passes[0].Label);
        Assert.Contains("-DeleteHLODs", passes[0].Arguments);
        Assert.DoesNotContain("-BuildHLODs", passes[0].Arguments);

        Assert.Equal("Setup and build HLODs", passes[1].Label);
        Assert.Contains("-SetupHLODs", passes[1].Arguments);
        Assert.Contains("-BuildHLODs", passes[1].Arguments);
        Assert.DoesNotContain("-DeleteHLODs", passes[1].Arguments);
    }

    /// <summary>Every pass names the project, the map and the commandlet.</summary>
    [Fact]
    public void Every_pass_carries_the_project_the_map_and_the_builder()
    {
        var passes = HlodBuilder.Passes(ProjectFile, Map, Options());

        Assert.All(passes, pass =>
        {
            Assert.Equal($"\"{ProjectFile}\"", pass.Arguments[0]);
            Assert.Equal(Map, pass.Arguments[1]);

            Assert.Contains("-run=WorldPartitionBuilderCommandlet", pass.Arguments);
            Assert.Contains($"-Builder={HlodBuilder.Simplygon}", pass.Arguments);
        });
    }

    /// <summary>
    /// The commandlet takes one map as a positional argument. Passing several is how the
    /// navigation page used to silently build one map and call the rest successful.
    /// </summary>
    [Fact]
    public void Each_pass_passes_exactly_one_map()
    {
        foreach (var pass in HlodBuilder.Passes(ProjectFile, Map, Options()))
        {
            var positional = pass.Arguments.Skip(1).TakeWhile(a => !a.StartsWith('-')).ToList();

            Assert.Single(positional);
            Assert.Equal(Map, positional[0]);
        }
    }

    [Fact]
    public void The_unreal_builder_only_changes_the_builder_name()
    {
        var unreal = HlodBuilder.Passes(ProjectFile, Map, Options(builder: HlodBuilder.Unreal));

        Assert.Contains($"-Builder={HlodBuilder.Unreal}", unreal[1].Arguments);
        Assert.DoesNotContain($"-Builder={HlodBuilder.Simplygon}", unreal[1].Arguments);

        // Same flags either way, so switching builder does not quietly change the build.
        Assert.Equal(
            HlodBuilder.Passes(ProjectFile, Map, Options()).Select(p => p.Arguments.Count),
            unreal.Select(p => p.Arguments.Count));
    }

    [Fact]
    public void Unticking_delete_leaves_only_the_build_pass()
    {
        var passes = HlodBuilder.Passes(ProjectFile, Map, Options(delete: false));

        Assert.Single(passes);
        Assert.Contains("-BuildHLODs", passes[0].Arguments);
    }

    /// <summary>Stripping HLODs from a level without rebuilding them is a real thing to want.</summary>
    [Fact]
    public void Deleting_without_rebuilding_is_a_single_pass()
    {
        var passes = HlodBuilder.Passes(ProjectFile, Map, Options(setup: false, build: false));

        Assert.Single(passes);
        Assert.Equal("Delete HLODs", passes[0].Label);
    }

    [Fact]
    public void Optional_flags_appear_only_when_ticked()
    {
        var all = HlodBuilder.BuildArguments(ProjectFile, Map, Options());

        Assert.Contains("-AllowCommandletRendering", all);
        Assert.Contains("-WaitMutex", all);
        Assert.Contains("-Unattended", all);

        var none = HlodBuilder.BuildArguments(
            ProjectFile, Map, Options(rendering: false, waitMutex: false, unattended: false));

        Assert.DoesNotContain("-AllowCommandletRendering", none);
        Assert.DoesNotContain("-WaitMutex", none);
        Assert.DoesNotContain("-Unattended", none);
    }

    /// <summary>
    /// The process runs with no window, so a splash or a source-control prompt would hang
    /// the build with nothing on screen to dismiss, and without -stdout the log dock stays
    /// empty for the whole run.
    /// </summary>
    [Fact]
    public void Every_pass_runs_headless()
    {
        Assert.All(HlodBuilder.Passes(ProjectFile, Map, Options()), pass =>
        {
            Assert.Contains("-NoSplash", pass.Arguments);
            Assert.Contains("-stdout", pass.Arguments);
            Assert.Contains("-SCCProvider=None", pass.Arguments);
        });
    }

    [Fact]
    public void Extra_arguments_are_appended_to_every_pass()
    {
        var passes = HlodBuilder.Passes(ProjectFile, Map, Options(extra: "  -DistributedBuild -BuilderIdx=0  "));

        Assert.All(passes, pass => Assert.Equal("-DistributedBuild -BuilderIdx=0", pass.Arguments[^1]));
    }

    [Fact]
    public void Validation_requires_a_project_a_map_and_something_to_do()
    {
        Assert.Contains(
            HlodBuilder.Validate(ProjectFile, Array.Empty<string>(), Options()),
            p => p.Contains("at least one map"));

        Assert.Contains(
            HlodBuilder.Validate(ProjectFile, new[] { Map }, Options(delete: false, setup: false, build: false)),
            p => p.Contains("Nothing to do"));

        Assert.Contains(
            HlodBuilder.Validate(ProjectFile, new[] { Map }, Options(builder: "SomeOtherBuilder")),
            p => p.Contains("SomeOtherBuilder"));
    }

    /// <summary>The dropdown shows a name; the preset stores the commandlet's own.</summary>
    [Fact]
    public void Builders_are_offered_by_display_name()
    {
        Assert.Equal("Simplygon", HlodBuilder.DisplayName(HlodBuilder.Simplygon));
        Assert.Contains("Unreal", HlodBuilder.DisplayName(HlodBuilder.Unreal));

        Assert.Equal(2, HlodBuilder.Builders.Count);
        Assert.All(HlodBuilder.Builders, b => Assert.False(string.IsNullOrWhiteSpace(b.Display)));
    }
}
