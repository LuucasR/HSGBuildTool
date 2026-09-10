using System;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The Blueprint command lines, and the two bits of vocabulary the log reading depends
/// on: turning the object path the commandlet prints into a package path, and telling
/// project content from engine content.
/// </summary>
public class BlueprintBuilderTests
{
    private const string ProjectFile = @"D:\Proj\FMFC.uproject";

    private static BlueprintOptions Options(
        bool update = true,
        bool nodes = true,
        bool skipEngine = false,
        string extra = "")
        => new(update, nodes, skipEngine, extra);

    [Fact]
    public void The_compile_step_names_the_project_and_the_commandlet()
    {
        var arguments = BlueprintBuilder.CompileAllArguments(ProjectFile, Options());

        Assert.Equal($"\"{ProjectFile}\"", arguments[0]);
        Assert.Contains("-run=CompileAllBlueprints", arguments);
    }

    /// <summary>
    /// The process runs with no window, so a splash or a source-control prompt would hang
    /// it invisibly, and without -stdout the log dock shows nothing at all.
    /// </summary>
    [Fact]
    public void Every_invocation_carries_the_headless_tail()
    {
        foreach (var arguments in new[]
                 {
                     BlueprintBuilder.CompileAllArguments(ProjectFile, Options()),
                     BlueprintBuilder.RefreshArguments(ProjectFile, @"D:\Logs\run.py", Options())
                 })
        {
            Assert.Contains("-SCCProvider=None", arguments);
            Assert.Contains("-NoSplash", arguments);
            Assert.Contains("-stdout", arguments);
            Assert.Contains("-Unattended", arguments);
            Assert.Contains("-WaitMutex", arguments);
        }
    }

    /// <summary>
    /// Insurance against a project that turned this category down in DefaultEngine.ini:
    /// without the per-asset line the whole step becomes unreadable.
    /// </summary>
    [Fact]
    public void The_compile_step_forces_the_per_asset_category_on()
    {
        var commandLine = BlueprintBuilder.ToCommandLine(BlueprintBuilder.CompileAllArguments(ProjectFile, Options()));

        Assert.Contains("-LogCmds=\"LogCompileAllBlueprintsCommandlet Display\"", commandLine);
    }

    /// <summary>
    /// The commandlet's own list of assets with problems. The page reads the per-Blueprint
    /// errors anyway; this is the second opinion that separates "nothing failed" from
    /// "the output stopped being readable".
    /// </summary>
    [Fact]
    public void The_compile_step_asks_for_the_asset_list()
    {
        Assert.Contains("-SimpleAssetList", BlueprintBuilder.CompileAllArguments(ProjectFile, Options()));
    }

    /// <summary>
    /// -ShowResultsOnly silences the compiler result log, which is exactly what emits the
    /// per-Blueprint error lines. Passing it would leave a run that names nothing.
    /// </summary>
    [Fact]
    public void The_compile_step_never_silences_the_per_blueprint_errors()
    {
        var commandLine = BlueprintBuilder.ToCommandLine(BlueprintBuilder.CompileAllArguments(ProjectFile, Options()));

        Assert.DoesNotContain("-ShowResultsOnly", commandLine);
    }

    [Fact]
    public void Skipping_engine_content_ignores_it_at_the_source()
    {
        var on = BlueprintBuilder.ToCommandLine(BlueprintBuilder.CompileAllArguments(ProjectFile, Options(skipEngine: true)));
        var off = BlueprintBuilder.ToCommandLine(BlueprintBuilder.CompileAllArguments(ProjectFile, Options(skipEngine: false)));

        // Comma-separated, and matched by the commandlet with StartsWith on the object path.
        Assert.Contains("-IgnoreFolder=/Engine/", on);
        Assert.DoesNotContain("-IgnoreFolder", off);
    }

    // ---------------------------------------------------------------- log lines

    /// <summary>Verbatim from CompileAllBlueprintsCommandlet::LogResults.</summary>
    [Theory]
    [InlineData("Compiling Completed with 3 errors and 12 warnings and 1 blueprints that failed to load.", 3, 12, 1)]
    [InlineData("Compiling Completed with 0 errors and 0 warnings and 0 blueprints that failed to load.", 0, 0, 0)]
    // Engine versions before 5.1 ended the sentence at the warning count.
    [InlineData("Compiling Completed with 2 errors and 5 warnings.", 2, 5, 0)]
    public void The_closing_line_yields_the_commandlets_totals(string line, int errors, int warnings, int failed)
    {
        Assert.Equal(new BlueprintTotals(errors, warnings, failed), BlueprintBuilder.TotalsFromSummaryLine(line));
    }

    [Fact]
    public void An_ordinary_line_is_not_a_summary()
    {
        Assert.Null(BlueprintBuilder.TotalsFromSummaryLine("LogInit: Display: Compiling shaders"));
    }

    /// <summary>The space before the colon is the commandlet's, not a typo.</summary>
    [Fact]
    public void A_load_failure_names_its_own_asset()
    {
        const string line = "LogCompileAllBlueprintsCommandlet: Error: Failed to Load : '/Game/BP/BP_Corrupt.BP_Corrupt'.";

        Assert.Equal("/Game/BP/BP_Corrupt", BlueprintBuilder.AssetFromFailedToLoadLine(line));
        Assert.Equal("", BlueprintBuilder.AssetFromFailedToLoadLine("LogInit: Error: something else"));
    }

    /// <summary>
    /// Each of these is its own UE_LOG, so unlike the block's header it keeps the category
    /// prefix. The rules and blank lines framing it must not be mistaken for assets.
    /// </summary>
    [Theory]
    [InlineData("LogCompileAllBlueprintsCommandlet: Warning: /Game/BP/BP_Foo.BP_Foo", "/Game/BP/BP_Foo")]
    [InlineData("===================================================================================", "")]
    [InlineData("", "")]
    public void An_asset_list_line_yields_its_package_path(string line, string expected)
    {
        Assert.Equal(expected, BlueprintBuilder.ListedAsset(line));
    }

    [Fact]
    public void The_refresh_step_quotes_a_script_path_with_spaces()
    {
        var arguments = BlueprintBuilder.RefreshArguments(ProjectFile, @"C:\My Logs\run.py", Options());

        Assert.Contains("-run=pythonscript", arguments);
        Assert.Contains("-script=\"C:\\My Logs\\run.py\"", arguments);
    }

    /// <summary>
    /// Appended verbatim rather than tokenised, and last, so a quoted value the user typed
    /// survives and nothing the tool adds can land after it.
    /// </summary>
    [Fact]
    public void Extra_arguments_are_appended_verbatim_and_last()
    {
        var arguments = BlueprintBuilder.CompileAllArguments(ProjectFile, Options(extra: "  -DirtyOnly  "));

        Assert.Equal("-DirtyOnly", arguments[^1]);
    }

    [Fact]
    public void The_refresh_pass_is_only_offered_with_a_script_to_run()
    {
        var without = BlueprintBuilder.Passes(ProjectFile, Options());
        var with = BlueprintBuilder.Passes(ProjectFile, Options(), @"D:\Logs\run.py");

        Assert.Single(without);
        Assert.Equal(2, with.Count);
        Assert.Equal("Reload and resave", with[1].Label);
    }

    [Fact]
    public void Nothing_runs_when_the_option_is_off()
    {
        Assert.Empty(BlueprintBuilder.Passes(ProjectFile, Options(update: false), @"D:\Logs\run.py"));
    }

    // ---------------------------------------------------------------- paths

    [Theory]
    [InlineData("/Game/BP/BP_Foo.BP_Foo", "/Game/BP/BP_Foo")]
    [InlineData("'/Game/BP/BP_Foo.BP_Foo'", "/Game/BP/BP_Foo")]
    [InlineData("/Game/BP/BP_Foo", "/Game/BP/BP_Foo")]
    [InlineData("", "")]
    public void An_object_path_becomes_a_package_path(string input, string expected)
    {
        Assert.Equal(expected, BlueprintBuilder.ToPackagePath(input));
    }

    [Fact]
    public void Normalising_a_package_path_again_changes_nothing()
    {
        var once = BlueprintBuilder.ToPackagePath("/Game/BP/BP_Foo.BP_Foo");

        Assert.Equal(once, BlueprintBuilder.ToPackagePath(once));
    }

    /// <summary>
    /// Only the "Name.Name" form is a package/object pair. Trimming anything else would
    /// quietly name a different asset, which is worse than leaving an odd path alone.
    /// </summary>
    [Fact]
    public void A_path_whose_object_name_differs_is_left_alone()
    {
        Assert.Equal("/Game/Foo.Bar", BlueprintBuilder.ToPackagePath("/Game/Foo.Bar"));
    }

    [Theory]
    [InlineData("/Game/BP/BP_Foo", true)]
    [InlineData("/Engine/EngineDamageTypes/DmgTypeBP_Environmental", false)]
    [InlineData("/Simplygon/BP_Thing", false)]
    public void Only_project_content_is_ours_to_fix(string path, bool expected)
    {
        Assert.Equal(expected, BlueprintBuilder.IsProjectContent(path));
    }

    /// <summary>Verbatim from a real commandlet log.</summary>
    [Fact]
    public void The_asset_is_read_out_of_the_commandlets_own_line()
    {
        const string line =
            "LogCompileAllBlueprintsCommandlet: Display: Loading and Compiling: '/Game/BP/BP_Door.BP_Door'...";

        Assert.Equal("/Game/BP/BP_Door", BlueprintBuilder.AssetFromLoadingLine(line));
        Assert.Equal("", BlueprintBuilder.AssetFromLoadingLine("LogInit: Display: Something else entirely"));
    }

    /// <summary>
    /// A Blueprint's generated class is "Foo.Foo_C", and load-time errors name that rather
    /// than the asset. It is still the same package.
    /// </summary>
    [Theory]
    [InlineData("/Game/BP/BP_Foo.BP_Foo_C", "/Game/BP/BP_Foo")]
    [InlineData("/Game/BP/BP_Foo.BP_Foo", "/Game/BP/BP_Foo")]
    public void A_generated_class_path_becomes_its_package(string input, string expected)
    {
        Assert.Equal(expected, BlueprintBuilder.ToPackagePath(input));
    }

    /// <summary>
    /// Verbatim from a real run, and the exact damage this page exists to find: a property
    /// whose struct no longer exists. It is logged while some *other* Blueprint is being
    /// compiled, so the line naming its own asset is the only way to get it on the right row.
    /// </summary>
    [Fact]
    public void A_load_error_that_names_its_asset_yields_that_asset()
    {
        const string line =
            "LogProperty: Error: FStructProperty::Serialize Loading: Property 'StructProperty " +
            "/Game/Gallery/BP/Widget/Pizzeria/WBP_OrderPizzeria.WBP_OrderPizzeria_C:PizzeriaTakingOrderDefinition'. " +
            "Unknown structure.";

        Assert.Equal(
            "/Game/Gallery/BP/Widget/Pizzeria/WBP_OrderPizzeria",
            BlueprintBuilder.AssetFromQuotedObjectPath(line));
    }

    [Fact]
    public void An_error_that_names_no_asset_yields_nothing()
    {
        Assert.Equal("", BlueprintBuilder.AssetFromQuotedObjectPath("LogBlueprint: Error: [Compiler] something broke"));
        Assert.Equal("", BlueprintBuilder.AssetFromQuotedObjectPath("LogInit: Error: 'not a path' went wrong"));
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void Refreshing_nodes_without_the_step_that_finds_them_is_flagged()
    {
        var problems = BlueprintBuilder.Validate(ProjectFile, null, null, Options(update: false, nodes: true));

        Assert.Contains(problems, p => p.Contains("Update all nodes"));
    }

    [Fact]
    public void Nothing_is_flagged_when_both_options_are_off()
    {
        Assert.Empty(BlueprintBuilder.Validate(ProjectFile, null, null, Options(update: false, nodes: false)));
    }

    /// <summary>
    /// The Blueprint steps launch the editor. A game target does not build editor modules,
    /// so every Blueprint would fail for a reason that has nothing to do with Blueprints.
    /// </summary>
    [Fact]
    public void A_game_target_is_flagged_because_the_editor_is_what_runs()
    {
        var problems = BlueprintBuilder.Validate(
            ProjectFile,
            null,
            new CompileTarget("FMFC", IsEditor: false),
            Options(nodes: false));

        Assert.Contains(problems, p => p.Contains("not an editor target"));
    }

    [Fact]
    public void An_editor_target_is_not_flagged()
    {
        var problems = BlueprintBuilder.Validate(
            ProjectFile,
            null,
            new CompileTarget("FMFCEditor", IsEditor: true),
            Options(nodes: false));

        Assert.DoesNotContain(problems, p => p.Contains("editor target"));
    }

    // ---------------------------------------------------------------- python plugin

    [Theory]
    [InlineData("{\"Plugins\":[{\"Name\":\"PythonScriptPlugin\",\"Enabled\":true}]}", true)]
    [InlineData("{\"Plugins\":[{\"Name\":\"pythonscriptplugin\",\"Enabled\":true}]}", true)]
    [InlineData("{\"Plugins\":[{\"Name\":\"PythonScriptPlugin\"}]}", true)]
    [InlineData("{\"Plugins\":[{\"Name\":\"PythonScriptPlugin\",\"Enabled\":false}]}", false)]
    [InlineData("{\"Plugins\":[{\"Name\":\"Simplygon\",\"Enabled\":true}]}", false)]
    [InlineData("{\"FileVersion\":3}", false)]
    [InlineData("not json at all {", false)]
    public void The_python_plugin_is_read_out_of_the_uproject(string json, bool expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"fmfc-{Guid.NewGuid():N}.uproject");

        try
        {
            File.WriteAllText(path, json);

            Assert.Equal(expected, BlueprintBuilder.IsPythonPluginEnabled(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// This only ever produces a warning, so a missing or unreadable file must answer
    /// "no" rather than take the page down.
    /// </summary>
    [Fact]
    public void A_missing_uproject_answers_no_rather_than_throwing()
    {
        Assert.False(BlueprintBuilder.IsPythonPluginEnabled(@"D:\nowhere\missing.uproject"));
    }
}
