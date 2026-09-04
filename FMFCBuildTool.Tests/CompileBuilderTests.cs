using System;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The compile command line is three positional arguments followed by switches, and getting
/// the order or the configuration mapping wrong produces a UBT usage error rather than a
/// build. Asserted here so it never has to be discovered by clicking COMPILE.
/// </summary>
public class CompileBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectFile;

    public CompileBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        _projectFile = Path.Combine(_root, "FMFC.uproject");

        File.WriteAllText(_projectFile, "{}");
    }

    // ---------------------------------------------------------------- arguments

    [Fact]
    public void Target_platform_and_configuration_come_first_and_in_order()
    {
        var args = CompileBuilder.BuildArguments(_projectFile, "FMFCEditor", "Development");

        Assert.Equal("FMFCEditor", args[0]);
        Assert.Equal("Win64", args[1]);
        Assert.Equal("Development", args[2]);
    }

    /// <summary>Project paths routinely contain spaces; an unquoted -Project silently truncates.</summary>
    [Fact]
    public void Project_path_is_quoted()
    {
        var args = CompileBuilder.BuildArguments(@"C:\My Games\FMFC.uproject", "FMFC", "Shipping");

        Assert.Contains(@"-Project=""C:\My Games\FMFC.uproject""", args);
    }

    [Fact]
    public void WaitMutex_is_passed_so_a_busy_editor_does_not_fail_the_build()
    {
        Assert.Contains("-WaitMutex", CompileBuilder.BuildArguments(_projectFile, "FMFC", "Development"));
    }

    /// <summary>
    /// -FromMsBuild prefixes diagnostics with "UnrealBuildTool: ", which defeats LogParser's
    /// anchored bare-prefix rule and turns every compile error into a grey Info line.
    /// -utf8output is a UAT switch and would warn once per run.
    /// </summary>
    [Fact]
    public void Switches_that_would_break_log_classification_are_not_passed()
    {
        var args = CompileBuilder.BuildArguments(_projectFile, "FMFC", "Development");

        Assert.DoesNotContain("-FromMsBuild", args);
        Assert.DoesNotContain("-utf8output", args);
    }

    // ---------------------------------------------------------------- configuration

    [Theory]
    [InlineData("Debug", "DebugGame")]
    [InlineData("Development", "Development")]
    [InlineData("Shipping", "Shipping")]
    public void Debug_builds_as_DebugGame_and_the_rest_pass_through(string ui, string ubt)
    {
        Assert.Equal(ubt, CompileBuilder.ToUbtConfiguration(ui));
        Assert.Equal(ubt, CompileBuilder.BuildArguments(_projectFile, "FMFC", ui)[2]);
    }

    // ---------------------------------------------------------------- discovery

    [Fact]
    public void Targets_come_from_Target_cs_files_with_the_editor_first()
    {
        WriteTarget("FMFC", "Game");
        WriteTarget("FMFCEditor", "Editor");

        var targets = CompileBuilder.DiscoverTargets(_projectFile);

        Assert.Equal(new[] { "FMFCEditor", "FMFC" }, targets.Select(t => t.Name));
        Assert.True(targets[0].IsEditor);
        Assert.False(targets[1].IsEditor);

        Assert.Equal("FMFCEditor", CompileBuilder.DefaultTarget(targets));
    }

    /// <summary>The type is declared in the file; the name is only a convention.</summary>
    [Fact]
    public void An_editor_target_is_recognised_whatever_it_is_called()
    {
        WriteTarget("FMFCTools", "Editor");

        var targets = CompileBuilder.DiscoverTargets(_projectFile);

        Assert.True(Assert.Single(targets).IsEditor);
    }

    [Fact]
    public void A_blueprint_only_project_has_no_targets()
    {
        Assert.Empty(CompileBuilder.DiscoverTargets(_projectFile));
        Assert.Equal("", CompileBuilder.DefaultTarget(Array.Empty<CompileTarget>()));
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void A_project_with_no_cpp_reports_that_there_is_nothing_to_compile()
    {
        var problems = CompileBuilder.Validate(
            _projectFile, Array.Empty<CompileTarget>(), "", "Development", Engine());

        Assert.Contains(problems, p => p.Contains("nothing to compile"));
    }

    /// <summary>Editor targets build Debug, DebugGame and Development only.</summary>
    [Fact]
    public void Shipping_is_rejected_for_an_editor_target()
    {
        var targets = new[] { new CompileTarget("FMFCEditor", IsEditor: true) };

        Assert.Contains(
            CompileBuilder.Validate(_projectFile, targets, "FMFCEditor", "Shipping", Engine()),
            p => p.Contains("Shipping"));
    }

    [Fact]
    public void Shipping_is_accepted_for_a_game_target()
    {
        var targets = new[] { new CompileTarget("FMFC", IsEditor: false) };

        Assert.Empty(CompileBuilder.Validate(_projectFile, targets, "FMFC", "Shipping", Engine()));
    }

    [Fact]
    public void A_target_that_no_longer_exists_is_reported_with_the_ones_that_do()
    {
        var targets = new[] { new CompileTarget("FMFCEditor", IsEditor: true) };

        var problems = CompileBuilder.Validate(_projectFile, targets, "FMFCOld", "Development", Engine());

        Assert.Contains(problems, p => p.Contains("FMFCOld") && p.Contains("FMFCEditor"));
    }

    [Fact]
    public void An_engine_without_Build_bat_cannot_compile()
    {
        var targets = new[] { new CompileTarget("FMFC", IsEditor: false) };

        var problems = CompileBuilder.Validate(
            _projectFile, targets, "FMFC", "Development", Engine(buildBat: @"C:\nope\Build.bat"));

        Assert.Contains(problems, p => p.Contains("Build.bat"));
    }

    [Fact]
    public void An_invalid_project_is_the_only_thing_reported()
    {
        var problems = CompileBuilder.Validate(
            @"C:\nope\Missing.uproject", Array.Empty<CompileTarget>(), "", "Development", Engine());

        Assert.Equal("Select a valid .uproject file.", Assert.Single(problems));
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Build.bat points at this test assembly so the File.Exists check passes.</summary>
    private static EnginePaths Engine(string? buildBat = null) => new()
    {
        Root = @"C:\UE_5.4",
        RunUAT = "unused",
        EditorCmd = "unused",
        BuildBat = buildBat ?? typeof(CompileBuilderTests).Assembly.Location
    };

    private void WriteTarget(string name, string type)
    {
        var source = Path.Combine(_root, "Source");

        Directory.CreateDirectory(source);

        File.WriteAllText(
            Path.Combine(source, $"{name}.Target.cs"),
            $$"""
              using UnrealBuildTool;

              public class {{name}}Target : TargetRules
              {
                  public {{name}}Target(TargetInfo Target) : base(Target)
                  {
                      Type = TargetType.{{type}};
                  }
              }
              """);
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
