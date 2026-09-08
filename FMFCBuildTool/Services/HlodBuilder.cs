using System.Collections.Generic;
using System.Linq;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>
/// Builds the UnrealEditor-Cmd command lines that generate World Partition HLODs for a
/// single map, either through Simplygon or through Unreal's own builder.
/// </summary>
/// <remarks>
/// Two invocations, not one. Deleting the existing HLODs and building the replacements
/// are separate runs of WorldPartitionBuilderCommandlet — passing -DeleteHLODs alongside
/// -BuildHLODs deletes and then has nothing to build. That is why this returns
/// <see cref="CommandletPass"/> values rather than a single argument list, and why
/// <see cref="CommandletPageViewModel"/> runs passes rather than maps.
///
/// Both builders take the same flags; only -Builder= differs. Simplygon needs its plugin
/// enabled in the project, which the page warns about rather than blocking on — a plugin
/// can be enabled in an inherited config this tool never reads.
/// </remarks>
public static class HlodBuilder
{
    public const string Simplygon = "SimplygonWorldPartitionBuilder";
    public const string Unreal = "WorldPartitionHLODsBuilder";

    public static readonly IReadOnlyList<HlodBuilderOption> Builders = new[]
    {
        new HlodBuilderOption(Simplygon, "Simplygon"),
        new HlodBuilderOption(Unreal, "Unreal (built-in)")
    };

    public static string DisplayName(string builder)
        => Builders.FirstOrDefault(b => b.Id == builder)?.Display ?? builder;

    /// <summary>
    /// The invocations for one map, in order: the delete pass first, when asked for, then
    /// the setup/build pass. Either can be absent — deleting without rebuilding is a
    /// legitimate way to strip HLODs from a level.
    /// </summary>
    public static IReadOnlyList<CommandletPass> Passes(string projectFile, string map, HlodOptions options)
    {
        var passes = new List<CommandletPass>();

        if (options.DeleteHlods)
            passes.Add(new CommandletPass("Delete HLODs", DeleteArguments(projectFile, map, options)));

        if (options.SetupHlods || options.BuildHlods)
            passes.Add(new CommandletPass(BuildPassLabel(options), BuildArguments(projectFile, map, options)));

        return passes;
    }

    public static IReadOnlyList<string> DeleteArguments(string projectFile, string map, HlodOptions options)
        => Compose(projectFile, map, options, new[] { "-DeleteHLODs" });

    public static IReadOnlyList<string> BuildArguments(string projectFile, string map, HlodOptions options)
    {
        var actions = new List<string>();

        if (options.SetupHlods)
            actions.Add("-SetupHLODs");

        if (options.BuildHlods)
            actions.Add("-BuildHLODs");

        return Compose(projectFile, map, options, actions);
    }

    public static IReadOnlyList<string> Validate(string projectFile, IReadOnlyCollection<string> maps, HlodOptions options)
    {
        var problems = new List<string>();

        if (!ProjectLoader.IsValidProject(projectFile))
            problems.Add("Select a valid .uproject file.");

        if (maps.Count == 0)
            problems.Add("Select at least one map to build HLODs for.");

        if (Builders.All(b => b.Id != options.Builder))
            problems.Add($"Unknown HLOD builder \"{options.Builder}\".");

        // All three unticked is a run that starts the editor and asks it to do nothing,
        // which looks exactly like a build that finished suspiciously fast.
        if (!options.DeleteHlods && !options.SetupHlods && !options.BuildHlods)
            problems.Add("Nothing to do — tick Delete, Setup or Build.");

        return problems;
    }

    private static string BuildPassLabel(HlodOptions options)
        => options is { SetupHlods: true, BuildHlods: true } ? "Setup and build HLODs"
            : options.SetupHlods ? "Setup HLODs"
            : "Build HLODs";

    /// <summary>
    /// Assembles one invocation: the fixed head, the optional flags in the order you
    /// would type them, the headless tail, and anything the page's extra-arguments box
    /// adds on the end.
    /// </summary>
    private static IReadOnlyList<string> Compose(
        string projectFile,
        string map,
        HlodOptions options,
        IReadOnlyList<string> actions)
    {
        var arguments = new List<string>
        {
            $"\"{projectFile}\"",
            map,
            "-run=WorldPartitionBuilderCommandlet",
            $"-Builder={options.Builder}"
        };

        if (options.AllowCommandletRendering)
            arguments.Add("-AllowCommandletRendering");

        arguments.AddRange(actions);

        if (options.WaitMutex)
            arguments.Add("-WaitMutex");

        if (options.Unattended)
            arguments.Add("-Unattended");

        // The process runs with CreateNoWindow, so a splash or a source-control prompt
        // would block the build with nothing on screen to dismiss. -stdout is what makes
        // the log dock show anything at all.
        arguments.Add("-SCCProvider=None");
        arguments.Add("-NoSplash");
        arguments.Add("-stdout");

        // Appended verbatim rather than tokenised: the command line is rebuilt by joining
        // on spaces, so a quoted path the user typed survives intact.
        var extra = options.ExtraArguments.Trim();

        if (extra.Length > 0)
            arguments.Add(extra);

        return arguments;
    }
}

/// <summary>One entry of the page's builder dropdown: the -Builder= value and its label.</summary>
public sealed record HlodBuilderOption(string Id, string Display);

/// <summary>Everything the HLOD page lets you change, as the builder wants it.</summary>
public sealed record HlodOptions(
    string Builder,
    bool DeleteHlods,
    bool SetupHlods,
    bool BuildHlods,
    bool AllowCommandletRendering,
    bool WaitMutex,
    bool Unattended,
    string ExtraArguments);
