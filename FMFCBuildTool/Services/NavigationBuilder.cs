using System.Collections.Generic;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>
/// Builds the UnrealEditor-Cmd command line that runs the World Partition
/// navigation data builder for a single map.
/// </summary>
/// <remarks>
/// The previous version appended every selected map to one invocation, separated by
/// spaces. WorldPartitionBuilderCommandlet takes the map as a single positional
/// argument, so selecting eight maps built one and silently reported success for the
/// rest. Callers now loop over maps and invoke this once per map.
/// </remarks>
public static class NavigationBuilder
{
    public static IReadOnlyList<string> BuildArguments(string projectFile, string map)
    {
        return new List<string>
        {
            $"\"{projectFile}\"",
            map,
            "-run=WorldPartitionBuilderCommandlet",
            "-Builder=WorldPartitionNavigationDataBuilder",
            "-AllowCommandletRendering",
            "-SCCProvider=None",

            // The process runs with CreateNoWindow, so any modal dialog would hang
            // the build invisibly. These keep it strictly headless.
            "-unattended",
            "-NoSplash",
            "-stdout"
        };
    }

    public static string ToCommandLine(IEnumerable<string> arguments) => string.Join(" ", arguments);

    public static IReadOnlyList<string> Validate(string projectFile, IReadOnlyCollection<string> maps)
    {
        var problems = new List<string>();

        if (!ProjectLoader.IsValidProject(projectFile))
            problems.Add("Select a valid .uproject file.");

        if (maps.Count == 0)
            problems.Add("Select at least one map to build navigation for.");

        return problems;
    }
}
