using System.IO;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

public class RunUATBuilderTests
{
    private const string ProjectFile = @"D:\Proj\FMFC.uproject";

    private static EnginePaths Engine(bool installed = true) => new()
    {
        Root = @"C:\UE_5.4",
        RunUAT = @"C:\UE_5.4\Engine\Build\BatchFiles\RunUAT.bat",
        EditorCmd = @"C:\UE_5.4\Engine\Binaries\Win64\UnrealEditor-Cmd.exe",
        Version = "5.4",
        IsInstalled = installed
    };

    private static BuildPreset Minimal() => new()
    {
        Build = false,
        Cook = true,
        Stage = false,
        Package = false,
        Archive = false,
        Pak = false,
        IoStore = false,
        Compressed = false,
        NoCompile = false,
        NoCompileEditor = false,
        SkipCookingEditorContent = false,
        UnversionedCookedContent = false,
        FileOpenLog = false,
        StdOut = false,
        CrashForUAT = false,
        Unattended = false,
        NoLogTimes = false,
        UseProjectDefaultMaps = true
    };

    [Fact]
    public void Always_emits_the_command_and_project()
    {
        var args = RunUATBuilder.BuildArguments(Minimal(), ProjectFile, Engine());

        Assert.Equal("BuildCookRun", args[0]);
        Assert.Contains($"-project=\"{ProjectFile}\"", args);
    }

    [Fact]
    public void Platform_comes_from_the_preset()
    {
        var preset = Minimal();
        preset.Platform = "Linux";

        Assert.Contains("-platform=Linux", RunUATBuilder.BuildArguments(preset, ProjectFile, Engine()));
    }

    /// <summary>
    /// -installed used to be emitted unconditionally, which is wrong for a source build.
    /// </summary>
    [Fact]
    public void Installed_flag_tracks_the_engine_kind()
    {
        Assert.Contains("-installed", RunUATBuilder.BuildArguments(Minimal(), ProjectFile, Engine(installed: true)));
        Assert.DoesNotContain("-installed", RunUATBuilder.BuildArguments(Minimal(), ProjectFile, Engine(installed: false)));
    }

    [Fact]
    public void Cultures_join_with_a_plus()
    {
        var preset = Minimal();
        preset.CookCultures = new() { "en", "es", "pt-BR" };

        Assert.Contains("-CookCultures=en+es+pt-BR", RunUATBuilder.BuildArguments(preset, ProjectFile, Engine()));
    }

    [Fact]
    public void Maps_join_with_a_plus_when_defaults_are_off()
    {
        var preset = Minimal();
        preset.UseProjectDefaultMaps = false;
        preset.Maps = new() { "/Game/Maps/A", "/Game/Maps/B" };

        Assert.Contains("-map=/Game/Maps/A+/Game/Maps/B", RunUATBuilder.BuildArguments(preset, ProjectFile, Engine()));
    }

    [Fact]
    public void Maps_are_omitted_when_project_defaults_are_used()
    {
        var preset = Minimal();
        preset.UseProjectDefaultMaps = true;
        preset.Maps = new() { "/Game/Maps/A" };

        Assert.DoesNotContain(
            RunUATBuilder.BuildArguments(preset, ProjectFile, Engine()),
            a => a.StartsWith("-map="));
    }

    [Fact]
    public void IoStore_is_reachable()
    {
        var preset = Minimal();
        preset.IoStore = true;

        // Hardcoded to false in the old code-behind, so this argument could never be produced.
        Assert.Contains("-iostore", RunUATBuilder.BuildArguments(preset, ProjectFile, Engine()));
    }

    [Fact]
    public void Full_cook_maps_to_clean()
    {
        var preset = Minimal();
        preset.FullCook = true;

        Assert.Contains("-clean", RunUATBuilder.BuildArguments(preset, ProjectFile, Engine()));
    }

    [Fact]
    public void Archive_directory_is_quoted()
    {
        var preset = Minimal();
        preset.Archive = true;
        preset.ArchiveDirectory = @"D:\Builds\With Space";

        var args = RunUATBuilder.BuildArguments(preset, ProjectFile, Engine());

        Assert.Contains("-archive", args);
        Assert.Contains(@"-archivedirectory=""D:\Builds\With Space""", args);
    }

    [Fact]
    public void Validation_flags_an_empty_archive_directory()
    {
        var preset = Minimal();
        preset.Archive = true;
        preset.ArchiveDirectory = "";

        var problems = RunUATBuilder.Validate(preset, ProjectFile);

        Assert.Contains(problems, p => p.Contains("archive folder"));
    }

    [Fact]
    public void Validation_flags_an_empty_pipeline()
    {
        var preset = Minimal();
        preset.Cook = false;

        Assert.Contains(RunUATBuilder.Validate(preset, ProjectFile), p => p.Contains("Nothing to do"));
    }

    [Fact]
    public void Validation_flags_cooking_with_no_maps_and_no_defaults()
    {
        var preset = Minimal();
        preset.UseProjectDefaultMaps = false;
        preset.Maps.Clear();

        Assert.Contains(RunUATBuilder.Validate(preset, ProjectFile), p => p.Contains("No maps selected"));
    }
}
