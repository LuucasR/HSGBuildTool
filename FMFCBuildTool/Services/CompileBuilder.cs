using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>One <c>&lt;Name&gt;.Target.cs</c> in the project's Source folder.</summary>
/// <param name="Name">The name UBT wants, i.e. the file name without <c>.Target.cs</c>.</param>
/// <param name="IsEditor">
/// Read from <c>Type = TargetType.Editor</c> in the file rather than guessed from the name,
/// because nothing forces an editor target to be called "…Editor".
/// </param>
public sealed record CompileTarget(string Name, bool IsEditor);

/// <summary>
/// Turns a target and a configuration into an <c>Engine\Build\BatchFiles\Build.bat</c>
/// (UnrealBuildTool) command line.
/// </summary>
/// <remarks>
/// The tool could package a project but never compile one, so a class that had never been
/// built only surfaced twenty minutes into a cook. Same shape as <see cref="RunUATBuilder"/> —
/// a token list rather than one string, so the Compiler page can show the exact command
/// before running it and the argument construction is testable without launching anything.
/// </remarks>
public static partial class CompileBuilder
{
    /// <summary>Unreal builds Windows from this tool and nothing here needs a picker.</summary>
    public const string Platform = "Win64";

    private const string TargetSuffix = ".Target.cs";

    /// <summary>
    /// What the page offers. "Debug" is deliberately not Unreal's Debug: that one needs an
    /// engine compiled in Debug and simply fails against a Launcher install, whereas
    /// DebugGame — debuggable game code against a Development engine — is what anyone
    /// picking "Debug" here actually wants.
    /// </summary>
    public static readonly IReadOnlyList<string> Configurations = new[] { "Debug", "Development", "Shipping" };

    public static string ToUbtConfiguration(string uiConfiguration)
        => uiConfiguration == "Debug" ? "DebugGame" : uiConfiguration;

    /// <summary>
    /// The targets this project declares. Empty for a Blueprint-only project, which is a
    /// real state the page explains rather than fails on.
    /// </summary>
    /// <remarks>
    /// Top directory only, matching UBT's own definition of "this project has source code".
    /// Searching recursively would surface plugin and program targets that UBT will not
    /// accept from this project, and the temporary ones it generates into
    /// Intermediate\Source for a Blueprint project that has a code plugin.
    /// </remarks>
    public static IReadOnlyList<CompileTarget> DiscoverTargets(string projectFile)
    {
        if (!ProjectLoader.IsValidProject(projectFile))
            return Array.Empty<CompileTarget>();

        var source = Path.Combine(ProjectLoader.GetProjectDirectory(projectFile), "Source");

        string[] files;

        try
        {
            if (!Directory.Exists(source))
                return Array.Empty<CompileTarget>();

            files = Directory.GetFiles(source, "*" + TargetSuffix, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            // Runs on every project switch; an unreadable Source folder is not worth a
            // stack trace. The page then says there is nothing to compile, which is as
            // much as it can tell.
            return Array.Empty<CompileTarget>();
        }

        return files
            // Not GetFileNameWithoutExtension, which turns "Foo.Target.cs" into "Foo.Target".
            .Select(file => new { File = file, Name = Path.GetFileName(file)! })
            .Where(x => x.Name.Length > TargetSuffix.Length)
            .Select(x => new CompileTarget(x.Name[..^TargetSuffix.Length], IsEditorTarget(x.File, x.Name)))

            // Editor first: it is what you compile to get new classes into the editor,
            // which is the reason this page exists.
            .OrderByDescending(t => t.IsEditor)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The editor target when the project has one, otherwise whatever it does have.</summary>
    public static string DefaultTarget(IReadOnlyList<CompileTarget> targets)
    {
        if (targets.Count == 0)
            return "";

        return (targets.FirstOrDefault(t => t.IsEditor) ?? targets[0]).Name;
    }

    /// <summary>Build.bat for this engine.</summary>
    public static string BuildBatFor(EnginePaths engine) => engine.BuildBat;

    /// <summary>
    /// Problems that would make the compile fail or do nothing, so the page can disable
    /// COMPILE and explain why instead of throwing on click.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        string projectFile,
        IReadOnlyList<CompileTarget> targets,
        string target,
        string configuration,
        EnginePaths? engine)
    {
        var problems = new List<string>();

        if (!ProjectLoader.IsValidProject(projectFile))
        {
            problems.Add("Select a valid .uproject file.");

            return problems;
        }

        var selected = targets.FirstOrDefault(t => t.Name.Equals(target, StringComparison.OrdinalIgnoreCase));

        if (targets.Count == 0)
        {
            problems.Add(@"This project has no C++ target (no Source\*.Target.cs), so there is nothing to compile.");
        }
        else if (string.IsNullOrWhiteSpace(target))
        {
            problems.Add("Select a build target.");
        }
        else if (selected is null)
        {
            problems.Add(
                $"The target \"{target}\" is not in Source. Available: {string.Join(", ", targets.Select(t => t.Name))}.");
        }

        if (!Configurations.Contains(configuration))
        {
            problems.Add($"Unknown configuration \"{configuration}\".");
        }
        else if (configuration == "Shipping" && selected is { IsEditor: true })
        {
            // Editor targets build Debug, DebugGame and Development only. UBT's own error
            // for this arrives several screens into the log, long after the click.
            problems.Add("Editor targets cannot be built Shipping. Use Development, or Debug to keep symbols.");
        }

        if (engine is not null && !File.Exists(engine.BuildBat))
        {
            problems.Add(
                $@"This engine has no Engine\Build\BatchFiles\Build.bat, so it cannot compile C++: {engine.Root}");
        }

        return problems;
    }

    /// <summary>
    /// Build.bat takes target, platform and configuration positionally and forwards
    /// everything after them straight to UnrealBuildTool.
    /// </summary>
    /// <remarks>
    /// Three switches were considered and deliberately left out.
    /// <c>-FromMsBuild</c> prefixes every diagnostic with "UnrealBuildTool: ", which defeats
    /// both the anchored bare-prefix rule and the case-sensitive category rule in
    /// <see cref="LogParser"/> — every UBT error would be logged as an uninteresting grey
    /// Info line and counted as neither error nor warning.
    /// <c>-utf8output</c> is a UAT switch, not a UBT one, and would add an "Invalid argument"
    /// warning to every single run.
    /// <c>-architecture=</c> appears in the generated .vcxproj only because MSBuild needs one
    /// row per architecture; UBT defaults to the host, which is what we want.
    /// </remarks>
    public static IReadOnlyList<string> BuildArguments(string projectFile, string target, string configuration)
    {
        return new List<string>
        {
            target,
            Platform,
            ToUbtConfiguration(configuration),

            // Required for a project outside the engine tree, and the path usually has spaces.
            $"-Project=\"{projectFile}\"",

            // UBT takes a global single-instance mutex. Without this it exits immediately
            // when the editor, Live Coding or a queued package build already holds it —
            // which is exactly the situation this page is used in.
            "-WaitMutex"
        };
    }

    public static string ToCommandLine(IEnumerable<string> arguments) => string.Join(" ", arguments);

    /// <summary>
    /// Explains the exit codes that are not a compile error, so the status line can say
    /// something better than "Failed (999)".
    /// </summary>
    public static string DescribeExitCode(int exitCode) => exitCode switch
    {
        10 => "another UnrealBuildTool instance is running",
        999 => "Build.bat could not start UnrealBuildTool — check that the .NET SDK is installed",
        _ => ""
    };

    /// <summary>
    /// Reads the declared target type. Falls back to the name when the file cannot be read,
    /// which is only a heuristic but is better than calling an editor target a game one.
    /// </summary>
    private static bool IsEditorTarget(string file, string fileName)
    {
        try
        {
            if (EditorTargetType().IsMatch(File.ReadAllText(file)))
                return true;

            return false;
        }
        catch
        {
            return fileName.StartsWith("Editor", StringComparison.OrdinalIgnoreCase) ||
                   fileName[..^TargetSuffix.Length].EndsWith("Editor", StringComparison.OrdinalIgnoreCase);
        }
    }

    [GeneratedRegex(@"Type\s*=\s*TargetType\.Editor\s*;")]
    private static partial Regex EditorTargetType();
}
