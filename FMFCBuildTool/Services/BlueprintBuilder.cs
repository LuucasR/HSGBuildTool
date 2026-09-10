using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>
/// Builds the two UnrealEditor-Cmd command lines the Compiler page runs after the C++
/// build, and owns the vocabulary both the log parsing and its tests read from.
/// </summary>
/// <remarks>
/// Compiling the C++ proves the C++ compiles. It says nothing about the Blueprints that
/// reference a USTRUCT whose members just moved, which stay broken until somebody opens
/// the editor — or until a cook falls over. Two steps close that:
///
/// <list type="number">
/// <item>CompileAllBlueprints loads and compiles every Blueprint and reports the failures.
/// It compiles in memory: nothing is written, so this step is a report, not a repair.</item>
/// <item>A generated Python script reloads the ones that failed, compiles them again and
/// saves them. That is as close to a repair as the editor's script API allows, and it
/// needs the Python plugin.</item>
/// </list>
///
/// Every switch and log line here was read out of UnrealEd's own
/// CompileAllBlueprintsCommandlet.cpp rather than guessed, which matters more than usual:
/// Unreal ignores an unknown switch silently, so a wrong guess would not fail — it would
/// quietly compile everything while the page claimed it had filtered.
///
/// One switch is deliberately absent. <c>-ShowResultsOnly</c> sets the compiler result
/// log to silent mode, which is precisely what suppresses the per-Blueprint
/// <c>LogBlueprint: Error:</c> lines this page reads to know which asset broke. Passing it
/// would leave a run that compiled everything and could name nothing.
/// </remarks>
public static partial class BlueprintBuilder
{
    /// <summary>The commandlet that loads and compiles every Blueprint in the project.</summary>
    public const string CompileAllCommandlet = "CompileAllBlueprints";

    /// <summary>
    /// Where the log names the asset it is about to compile. Verbatim from the commandlet:
    /// <c>LogCompileAllBlueprintsCommandlet: Display: Loading and Compiling: '/Game/X/BP_Foo.BP_Foo'...</c>
    /// </summary>
    public const string LoadingAndCompilingSentinel = "Loading and Compiling:";

    /// <summary>
    /// The commandlet's closing line, and the strongest evidence that it ran to the end:
    /// <c>Compiling Completed with 3 errors and 12 warnings and 0 blueprints that failed to load.</c>
    /// </summary>
    /// <remarks>
    /// It arrives with no category prefix. The UE_LOG that writes it embeds newlines in one
    /// message, so the log device stamps the category on the empty first line and the text
    /// itself comes through bare — which is why everything here matches on substrings
    /// rather than on <see cref="LogEntry.Category"/>.
    /// </remarks>
    public const string CompletedSentinel = "Compiling Completed with";

    /// <summary>Opens the -SimpleAssetList block. Bare, for the reason above.</summary>
    public const string AssetListStart = "Assets With Errors or Warnings:";

    public const string AssetListEnd = "End of Asset List";

    /// <summary>
    /// <c>Failed to Load : '%s'.</c> — an asset that never got as far as compiling. The
    /// space before the colon is the commandlet's, not a typo here.
    /// </summary>
    public const string FailedToLoadSentinel = "Failed to Load :";

    // The Python script's own markers. Deliberately free of the words Error, Warning and
    // Fatal: LogParser classifies any "Identifier: Verbosity:" pair, so a marker carrying
    // one would be logged as an error and counted against the very Blueprint it names.
    public const string BeginMarker = "FMFC-BP-BEGIN:";
    public const string EndMarker = "FMFC-BP-END:";
    public const string SummaryMarker = "FMFC-BP-SUMMARY:";

    // The per-asset outcomes the script can actually know. Whether the Blueprint still
    // fails to compile is not among them — compile_blueprint() returns None — so that
    // verdict is left to the errors logged between the two markers.
    public const string ResultOk = "OK";
    public const string ResultLoadFailed = "LOAD-FAILED";
    public const string ResultSaveFailed = "SAVE-FAILED";
    public const string ResultException = "EXCEPTION";

    /// <summary>Content this project owns and can actually fix.</summary>
    public const string ProjectContentRoot = "/Game/";

    /// <summary>What Unreal appends to a Blueprint's generated class.</summary>
    private const string GeneratedClassSuffix = "_C";

    /// <summary>
    /// What "skip engine content" hands to -IgnoreFolder.
    /// </summary>
    /// <remarks>
    /// Only /Engine/. -IgnoreFolder is a blocklist matched with StartsWith against the
    /// asset's object path, so it cannot express "only /Game" — plugin content mounts
    /// under its own root and is still compiled. That is why the option is named for what
    /// it does rather than for the narrower thing it looks like, and why plugin failures
    /// are dropped from the list on the C# side instead.
    /// </remarks>
    public static readonly IReadOnlyList<string> IgnoredFolders = new[] { "/Engine/" };

    /// <summary>
    /// Categories whose errors are never a Blueprint's fault. A headless editor start-up
    /// is full of them, and attributing one to whichever asset happened to be in flight
    /// would send someone to debug a Blueprint over a missing shader cache.
    /// </summary>
    /// <remarks>
    /// The last two are here because a real run put them there: a commandlet started
    /// against a large project logged <c>LogUObjectGlobals: Error: Ignored
    /// DoNotCreateDefaultSubobject…</c> while loading modules, and a run of
    /// <c>LogAssetRegistry: Error: Package is unloadable…</c> while scanning content. Both
    /// are about the project, neither is about the Blueprint being compiled.
    /// </remarks>
    public static readonly IReadOnlySet<string> NoiseCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "LogShaderCompilers",
        "LogDerivedDataCache",
        "LogSlate",
        "LogInit",
        "LogWindows",
        "LogHttp",
        "LogPakFile",
        "LogOutputDevice",
        "LogD3D12RHI",
        "LogRHI",
        "LogAudioMixer",
        "LogTurnkeySupport",
        "LogUObjectGlobals",
        "LogAssetRegistry"
    };

    /// <summary>
    /// Loads and compiles every Blueprint in the project, the way opening the editor
    /// would have found out.
    /// </summary>
    /// <remarks>
    /// -LogCmds is insurance rather than decoration: the per-asset line is logged at
    /// Display, and a project that has turned that category down in DefaultEngine.ini
    /// would produce a run this tool could not read. It would then be reported as
    /// inconclusive, which is correct but useless.
    /// </remarks>
    public static IReadOnlyList<string> CompileAllArguments(string projectFile, BlueprintOptions options)
    {
        var arguments = new List<string>
        {
            $"\"{projectFile}\"",
            $"-run={CompileAllCommandlet}",
            $"-LogCmds=\"Log{CompileAllCommandlet}Commandlet Display\"",

            // Prints the assets with problems as a plain list at the end. The page reads the
            // per-Blueprint errors anyway; this is the independent second opinion that tells
            // a run which found nothing apart from one whose output stopped being readable.
            "-SimpleAssetList"
        };

        if (options.SkipEngineContent)
        {
            // Matched with StartsWith against the asset's object path, comma-separated.
            // Skipping engine content at the source rather than filtering it out of the
            // results is both faster and honest: a broken engine Blueprint is not something
            // this project can fix, and compiling it every time to say so is waste.
            arguments.Add($"-IgnoreFolder={string.Join(",", IgnoredFolders)}");
        }

        AppendTail(arguments, options);

        return arguments;
    }

    /// <summary>Runs a generated Python script through the editor's pythonscript commandlet.</summary>
    public static IReadOnlyList<string> RefreshArguments(string projectFile, string scriptPath, BlueprintOptions options)
    {
        var arguments = new List<string>
        {
            $"\"{projectFile}\"",
            "-run=pythonscript",
            $"-script=\"{scriptPath}\""
        };

        AppendTail(arguments, options);

        return arguments;
    }

    /// <summary>
    /// The Blueprint steps of a run, as passes. The refresh pass is only ever included
    /// with a script path, because outside a live run there is no failure list to build
    /// one from.
    /// </summary>
    public static IReadOnlyList<CommandletPass> Passes(string projectFile, BlueprintOptions options, string scriptPath = "")
    {
        var passes = new List<CommandletPass>();

        if (!options.UpdateBlueprints)
            return passes;

        passes.Add(new CommandletPass("Update Blueprints", CompileAllArguments(projectFile, options)));

        if (options.UpdateAllNodes && scriptPath.Length > 0)
            passes.Add(new CommandletPass("Reload and resave", RefreshArguments(projectFile, scriptPath, options)));

        return passes;
    }

    /// <summary>
    /// Problems with the Blueprint options, so the page can say them instead of producing
    /// a run that silently does nothing useful.
    /// </summary>
    /// <param name="target">The selected compile target, needed for the editor-modules warning.</param>
    public static IReadOnlyList<string> Validate(
        string projectFile,
        EnginePaths? engine,
        CompileTarget? target,
        BlueprintOptions options)
    {
        var problems = new List<string>();

        if (!options.UpdateBlueprints)
        {
            // The second option on its own would look armed and do nothing.
            if (options.UpdateAllNodes)
                problems.Add("\"Update all nodes in Blueprint\" needs \"Update Blueprints\" — it works from that step's failures.");

            return problems;
        }

        if (engine is not null && !File.Exists(engine.EditorCmd))
        {
            problems.Add($"This engine has no UnrealEditor-Cmd.exe, so Blueprints cannot be compiled: {engine.Root}");
        }

        // The Blueprint steps launch the editor, which loads editor modules. A game target
        // does not build those, so the editor either refuses to start or runs stale code —
        // and every Blueprint then fails for a reason that has nothing to do with Blueprints.
        if (target is { IsEditor: false })
        {
            problems.Add(
                $"\"{target.Name}\" is not an editor target, so compiling it does not update the editor modules the Blueprint steps load.");
        }

        if (options.UpdateAllNodes && !IsPythonPluginEnabled(projectFile))
        {
            // A warning, not a block: a plugin can be enabled by an engine default or an
            // inherited config this tool never reads, the same reason the HLOD page warns
            // about Simplygon rather than refusing to run.
            problems.Add("This .uproject does not enable the Python Editor Script plugin, which the node refresh needs.");
        }

        return problems;
    }

    /// <summary>
    /// Whether the .uproject enables PythonScriptPlugin. False for anything unreadable —
    /// this only ever produces a warning, so guessing wrong must not throw.
    /// </summary>
    public static bool IsPythonPluginEnabled(string projectFile)
    {
        try
        {
            if (!ProjectLoader.IsValidProject(projectFile))
                return false;

            using var document = JsonDocument.Parse(File.ReadAllText(projectFile));

            if (!document.RootElement.TryGetProperty("Plugins", out var plugins) ||
                plugins.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var plugin in plugins.EnumerateArray())
            {
                if (plugin.ValueKind != JsonValueKind.Object)
                    continue;

                if (!plugin.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String)
                    continue;

                if (!string.Equals(name.GetString(), "PythonScriptPlugin", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Absent "Enabled" means enabled in the .uproject schema.
                return !plugin.TryGetProperty("Enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Turns the object path the log prints into the package path everything else wants:
    /// "/Game/X/BP_Foo.BP_Foo" becomes "/Game/X/BP_Foo".
    /// </summary>
    /// <remarks>
    /// Only the trailing "Name.Name" form is trimmed. A path whose object name differs
    /// from its package name is left alone: it is not the shape this is for, and dropping
    /// the suffix would silently name a different asset.
    /// </remarks>
    public static string ToPackagePath(string objectPath)
    {
        if (string.IsNullOrWhiteSpace(objectPath))
            return "";

        var path = objectPath.Trim().Trim('\'', '"');

        var dot = path.LastIndexOf('.');

        if (dot <= 0 || dot == path.Length - 1)
            return path;

        var package = path[..dot];
        var name = path[(dot + 1)..];
        var slash = package.LastIndexOf('/');
        var leaf = slash >= 0 ? package[(slash + 1)..] : package;

        // A Blueprint's generated class is "Foo.Foo_C", and load-time errors name that
        // rather than the asset. It is still the same package.
        if (name.EndsWith(GeneratedClassSuffix, StringComparison.Ordinal))
            name = name[..^GeneratedClassSuffix.Length];

        return string.Equals(leaf, name, StringComparison.Ordinal) ? package : path;
    }

    /// <summary>
    /// True for content this project owns. Engine and plugin Blueprints are compiled by
    /// the commandlet too, and listing one under "these are broken" sends someone to fix
    /// an asset they do not own and cannot save.
    /// </summary>
    public static bool IsProjectContent(string packagePath)
        => packagePath.StartsWith(ProjectContentRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>The asset named by a "Loading and Compiling:" line, or empty if this is not one.</summary>
    public static string AssetFromLoadingLine(string line)
    {
        var match = LoadingAndCompiling().Match(line);

        return match.Success ? ToPackagePath(match.Groups["path"].Value) : "";
    }

    /// <summary>
    /// The totals from the commandlet's closing line, or null when this is not that line.
    /// </summary>
    /// <remarks>
    /// Its counts are the cross-check that matters: if it reports errors and the page
    /// attributed none, the log stopped being readable somewhere and the run has to say so
    /// rather than show an empty list.
    /// </remarks>
    public static BlueprintTotals? TotalsFromSummaryLine(string line)
    {
        var match = CompletedSummary().Match(line);

        if (!match.Success)
            return null;

        // The failed-loads clause is optional: engine versions before 5.1 ended the
        // sentence at the warning count, and a run against one of those is still readable.
        var failed = match.Groups["failed"];

        return new BlueprintTotals(
            int.Parse(match.Groups["errors"].Value),
            int.Parse(match.Groups["warnings"].Value),
            failed.Success ? int.Parse(failed.Value) : 0);
    }

    /// <summary>
    /// The asset on one line of the -SimpleAssetList block, or empty for the rules and
    /// blank lines that frame it.
    /// </summary>
    /// <remarks>
    /// Each of these is its own UE_LOG, so unlike the block's header it arrives with the
    /// category prefix still attached: <c>LogCompileAllBlueprintsCommandlet: Warning: /Game/X/BP_Foo.BP_Foo</c>.
    /// </remarks>
    public static string ListedAsset(string line)
    {
        var match = TrailingObjectPath().Match(line);

        return match.Success ? ToPackagePath(match.Groups["path"].Value) : "";
    }

    /// <summary>
    /// The asset a load-time error names for itself, or empty when it names none.
    /// </summary>
    /// <remarks>
    /// These are the ones worth catching. A real run produced
    /// <c>LogProperty: Error: FStructProperty::Serialize Loading: Property 'StructProperty
    /// /Game/…/WBP_OrderPizzeria.WBP_OrderPizzeria_C:PizzeriaTakingOrderDefinition'. Unknown
    /// structure.</c> — a property whose struct no longer exists, which is precisely the
    /// damage this page was built to find. It surfaces while some *other* Blueprint is being
    /// compiled and drags the broken one in as a dependency, so attributing it by position
    /// would blame the wrong asset. The line names the right one; believe the line.
    /// </remarks>
    public static string AssetFromQuotedObjectPath(string line)
    {
        var match = QuotedObjectPath().Match(line);

        return match.Success ? ToPackagePath(match.Groups["path"].Value) : "";
    }

    /// <summary>The asset named by a "Failed to Load :" line, or empty if this is not one.</summary>
    public static string AssetFromFailedToLoadLine(string line)
    {
        var match = FailedToLoad().Match(line);

        return match.Success ? ToPackagePath(match.Groups["path"].Value) : "";
    }

    public static string ToCommandLine(IEnumerable<string> arguments) => string.Join(" ", arguments);

    /// <summary>
    /// The headless tail every invocation shares, matching <see cref="HlodBuilder"/>:
    /// the process runs with CreateNoWindow, so a splash or a source-control prompt would
    /// block the run with nothing on screen to dismiss, and -stdout is what makes the log
    /// dock show anything at all.
    /// </summary>
    private static void AppendTail(List<string> arguments, BlueprintOptions options)
    {
        // UBT and the editor share one global mutex, and this runs seconds after a compile.
        arguments.Add("-WaitMutex");
        arguments.Add("-Unattended");
        arguments.Add("-SCCProvider=None");
        arguments.Add("-NoSplash");
        arguments.Add("-stdout");

        // Appended verbatim rather than tokenised, so a quoted path the user typed survives.
        var extra = options.ExtraArguments.Trim();

        if (extra.Length > 0)
            arguments.Add(extra);
    }

    [GeneratedRegex(@"Loading and Compiling:\s*'(?<path>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex LoadingAndCompiling();

    [GeneratedRegex(@"Failed to Load\s*:\s*'(?<path>[^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex FailedToLoad();

    /// <summary>A content path as the last thing on the line, which is all these lines are.</summary>
    [GeneratedRegex(@"(?<path>/[A-Za-z0-9_.\-/]+)\s*$")]
    private static partial Regex TrailingObjectPath();

    /// <summary>
    /// A rooted content path inside single quotes, stopping at the subobject separator so
    /// "…/WBP_Foo.WBP_Foo_C:SomeProperty" yields the asset rather than the property.
    /// </summary>
    [GeneratedRegex(@"'(?:[A-Za-z]+\s+)?(?<path>/[A-Za-z0-9_.\-/]+)(?::[^']*)?'")]
    private static partial Regex QuotedObjectPath();

    [GeneratedRegex(
        @"Compiling Completed with (?<errors>\d+) errors? and (?<warnings>\d+) warnings?(?: and (?<failed>\d+) blueprints? that failed to load)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex CompletedSummary();
}

/// <summary>The commandlet's own tally, from its closing line.</summary>
public readonly record struct BlueprintTotals(int Errors, int Warnings, int FailedLoads);

/// <summary>What the Compiler page's Blueprint section lets you change.</summary>
public sealed record BlueprintOptions(
    bool UpdateBlueprints,
    bool UpdateAllNodes,
    bool SkipEngineContent,
    string ExtraArguments)
{
    public static readonly BlueprintOptions None = new(false, false, true, "");
}
