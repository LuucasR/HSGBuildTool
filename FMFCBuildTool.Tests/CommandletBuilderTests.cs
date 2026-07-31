using System.Linq;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

public class CommandletBuilderTests
{
    private const string ProjectFile = @"D:\Proj\FMFC.uproject";

    /// <summary>
    /// The old builder appended every selected map to a single invocation as
    /// space-separated positional arguments. WorldPartitionBuilderCommandlet takes one
    /// map, so eight selected maps built one and silently "succeeded" for the rest.
    /// </summary>
    [Fact]
    public void Navigation_passes_exactly_one_map_per_invocation()
    {
        var args = NavigationBuilder.BuildArguments(ProjectFile, "/Game/Maps/L_Arena");

        var positional = args
            .Skip(1)
            .TakeWhile(a => !a.StartsWith('-'))
            .ToList();

        Assert.Single(positional);
        Assert.Equal("/Game/Maps/L_Arena", positional[0]);
    }

    [Fact]
    public void Navigation_quotes_the_project_and_names_the_builder()
    {
        var args = NavigationBuilder.BuildArguments(ProjectFile, "/Game/Maps/L_Arena");

        Assert.Equal($"\"{ProjectFile}\"", args[0]);
        Assert.Contains("-run=WorldPartitionBuilderCommandlet", args);
        Assert.Contains("-Builder=WorldPartitionNavigationDataBuilder", args);
    }

    /// <summary>The process runs with no window, so a modal dialog would hang it invisibly.</summary>
    [Fact]
    public void Commandlets_run_headless()
    {
        var nav = NavigationBuilder.BuildArguments(ProjectFile, "/Game/Maps/A");
        var lighting = LightingBuilder.BuildArguments(ProjectFile, "/Game/Maps/A", "Production");

        Assert.Contains("-unattended", nav);
        Assert.Contains("-NoSplash", nav);
        Assert.Contains("-unattended", lighting);
        Assert.Contains("-NoSplash", lighting);
    }

    [Fact]
    public void Navigation_validation_requires_a_map()
    {
        Assert.Contains(
            NavigationBuilder.Validate(ProjectFile, System.Array.Empty<string>()),
            p => p.Contains("at least one map"));
    }

    [Fact]
    public void Lighting_passes_the_quality_and_a_single_map()
    {
        var args = LightingBuilder.BuildArguments(ProjectFile, "/Game/Maps/L_Arena", "Medium");

        Assert.Contains("-buildlighting", args);
        Assert.Contains("-Quality=Medium", args);
        Assert.Contains("-Map=/Game/Maps/L_Arena", args);
    }

    [Fact]
    public void Lighting_validation_rejects_an_unknown_quality()
    {
        var problems = LightingBuilder.Validate(ProjectFile, new[] { "/Game/Maps/A" }, "Ultra");

        Assert.Contains(problems, p => p.Contains("Ultra"));
    }
}
