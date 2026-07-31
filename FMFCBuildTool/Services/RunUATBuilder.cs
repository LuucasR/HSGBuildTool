using System.Collections.Generic;
using System.Linq;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>
/// Turns a <see cref="BuildPreset"/> into a RunUAT BuildCookRun command line.
/// </summary>
/// <remarks>
/// Returns a token list rather than one concatenated string so the Package page can
/// show the exact command before running it, and so the argument construction can be
/// unit-tested without launching anything. Each token is already quoted where needed;
/// <see cref="ToCommandLine"/> just joins them with spaces, preserving the quoting
/// RunUAT.bat expects.
/// </remarks>
public static class RunUATBuilder
{
    /// <summary>
    /// Problems that would make the build fail or silently produce nothing.
    /// Lets the page disable BUILD and explain why, instead of throwing on click.
    /// </summary>
    public static IReadOnlyList<string> Validate(BuildPreset preset, string projectFile)
    {
        var problems = new List<string>();

        if (!ProjectLoader.IsValidProject(projectFile))
            problems.Add("Select a valid .uproject file.");

        if (preset.Archive && string.IsNullOrWhiteSpace(preset.ArchiveDirectory))
            problems.Add("Archive is enabled but no archive folder is set.");

        if (!preset.Build && !preset.Cook && !preset.Stage && !preset.Package && !preset.Archive)
            problems.Add("Nothing to do: enable at least one pipeline stage.");

        if (preset.CookCultures.Count == 0)
            problems.Add("Select at least one cook culture.");

        if (preset.Cook && !preset.UseProjectDefaultMaps && preset.Maps.Count == 0)
            problems.Add("No maps selected. Either pick maps or enable \"Use project default maps\".");

        return problems;
    }

    public static IReadOnlyList<string> BuildArguments(BuildPreset preset, string projectFile, EnginePaths engine)
    {
        var args = new List<string>
        {
            "BuildCookRun",
            $"-project=\"{projectFile}\"",
            "-noP4",
            $"-platform={preset.Platform}"
        };

        if (preset.CookCultures.Count > 0)
            args.Add($"-CookCultures={string.Join("+", preset.CookCultures)}");

        args.Add($"-clientconfig={preset.Configuration}");
        args.Add($"-serverconfig={preset.Configuration}");

        // Only meaningful for a binary engine. Passing it against a source build
        // (as the old code always did) makes UAT look for prebuilt binaries that
        // aren't there.
        if (engine.IsInstalled)
            args.Add("-installed");

        args.Add("-utf8output");

        if (!string.IsNullOrWhiteSpace(engine.EditorCmd))
            args.Add($"-unrealexe=\"{engine.EditorCmd}\"");

        AddFlag(args, preset.NoCompile, "-nocompile");
        AddFlag(args, preset.SkipCookingEditorContent, "-SkipCookingEditorContent");
        AddFlag(args, preset.NoCompileEditor, "-nocompileeditor");
        AddFlag(args, preset.UnversionedCookedContent, "-unversionedcookedcontent");
        AddFlag(args, preset.CookIncremental, "-cookincremental");
        AddFlag(args, preset.ZenStore, "-ZenStore");

        AddFlag(args, preset.Build, "-build");
        AddFlag(args, preset.Cook, "-cook");
        AddFlag(args, preset.FullCook, "-clean");
        AddFlag(args, preset.Stage, "-stage");
        AddFlag(args, preset.Package, "-package");
        AddFlag(args, preset.Pak, "-pak");
        AddFlag(args, preset.IoStore, "-iostore");
        AddFlag(args, preset.Prereqs, "-prereqs");
        AddFlag(args, preset.Distribution, "-distribution");
        AddFlag(args, preset.CrashReporter, "-crashreporter");
        AddFlag(args, preset.Server, "-server");
        AddFlag(args, preset.Client, "-client");
        AddFlag(args, preset.Compressed, "-compressed");

        AddFlag(args, preset.FileOpenLog, "-fileopenlog");
        AddFlag(args, preset.StdOut, "-stdout");
        AddFlag(args, preset.CrashForUAT, "-CrashForUAT");
        AddFlag(args, preset.Unattended, "-unattended");
        AddFlag(args, preset.NoLogTimes, "-NoLogTimes");

        if (!preset.UseProjectDefaultMaps && preset.Maps.Count > 0)
            args.Add($"-map={string.Join("+", preset.Maps)}");

        if (preset.Archive)
        {
            args.Add("-archive");
            args.Add($"-archivedirectory=\"{preset.ArchiveDirectory}\"");
        }

        return args;
    }

    public static string ToCommandLine(IEnumerable<string> arguments) => string.Join(" ", arguments);

    private static void AddFlag(ICollection<string> args, bool enabled, string flag)
    {
        if (enabled)
            args.Add(flag);
    }
}
