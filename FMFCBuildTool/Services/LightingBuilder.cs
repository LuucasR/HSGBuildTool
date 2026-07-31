using System.Collections.Generic;
using System.Linq;

namespace FMFCBuildTool.Services;

/// <summary>
/// Builds the UnrealEditor-Cmd command line that rebuilds static lighting for a
/// single map via the ResavePackages commandlet.
/// </summary>
/// <remarks>
/// One map per invocation, matching <see cref="NavigationBuilder"/>: the commandlet's
/// map handling varies between engine versions, and a per-map loop lets a single bad
/// map be reported instead of taking the whole run down.
///
/// Note this only does something for projects using baked/static lighting. On a
/// Lumen-only project there is nothing to build and the run will finish immediately.
/// </remarks>
public static class LightingBuilder
{
    public static readonly IReadOnlyList<string> Qualities = new[] { "Preview", "Medium", "High", "Production" };

    public static IReadOnlyList<string> BuildArguments(string projectFile, string map, string quality)
    {
        return new List<string>
        {
            $"\"{projectFile}\"",
            "-run=ResavePackages",
            "-buildlighting",
            $"-Quality={quality}",
            $"-Map={map}",
            "-AllowCommandletRendering",
            "-MapsOnly",
            "-ProjectOnly",
            "-SCCProvider=None",
            "-unattended",
            "-NoSplash",
            "-stdout"
        };
    }

    public static string ToCommandLine(IEnumerable<string> arguments) => string.Join(" ", arguments);

    public static IReadOnlyList<string> Validate(string projectFile, IReadOnlyCollection<string> maps, string quality)
    {
        var problems = new List<string>();

        if (!ProjectLoader.IsValidProject(projectFile))
            problems.Add("Select a valid .uproject file.");

        if (maps.Count == 0)
            problems.Add("Select at least one map to build lighting for.");

        if (!Qualities.Contains(quality))
            problems.Add($"Unknown lighting quality \"{quality}\".");

        return problems;
    }
}
