using System.Collections.Generic;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// Reading a Blueprint step's log.
/// </summary>
/// <remarks>
/// The failure mode these tests exist for is a clean green run produced by a parser that
/// stopped working. Unreal's log format is not a contract, so "we could not read it" has
/// to be a distinct, loud answer from "nothing failed" — and attributing an unrelated
/// engine error to whichever asset was in flight has to not happen at all.
/// </remarks>
public class BlueprintFailureCollectorTests
{
    /// <summary>Feeds lines through the real parser, the way a run does.</summary>
    private static BlueprintPassReport Run(BlueprintPassKind kind, IEnumerable<string> lines, int exitCode = 0, bool saving = true)
    {
        var collector = new BlueprintFailureCollector(kind, saving);

        foreach (var line in lines)
            collector.Observe(LogParser.Parse(line));

        return collector.Complete(exitCode);
    }

    private static string Loading(string objectPath)
        => $"LogCompileAllBlueprintsCommandlet: Display: Loading and Compiling: '{objectPath}'...";

    /// <summary>
    /// The commandlet's closing line. Verbatim, including the fact that it arrives with no
    /// category prefix: the UE_LOG that writes it embeds newlines, so the log device stamps
    /// the category on the empty first line and this text comes through bare.
    /// </summary>
    private static string Completed(int errors, int warnings, int failedLoads = 0)
        => $"Compiling Completed with {errors} errors and {warnings} warnings and {failedLoads} blueprints that failed to load.";

    private static string CompilerError(string message)
        => $"LogBlueprint: Error: [Compiler] {message}";

    // ---------------------------------------------------------------- compile step

    [Fact]
    public void The_blueprints_that_logged_errors_are_the_ones_reported()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            "LogCompileAllBlueprintsCommandlet: Display: Gathering All Blueprints From Asset Registry...",
            Loading("/Game/BP/BP_Fine.BP_Fine"),
            "LogBlueprint: Display: nothing to see here",
            Loading("/Game/BP/BP_Broken.BP_Broken"),
            CompilerError("Struct member Health not found"),
            Loading("/Game/BP/BP_AlsoFine.BP_AlsoFine"),
            Completed(1, 0)
        });

        Assert.True(report.Conclusive);
        Assert.Equal(3, report.Attempted);

        var row = Assert.Single(report.Results);

        Assert.Equal("/Game/BP/BP_Broken", row.Path);
        Assert.Equal("BP_Broken", row.Name);
        Assert.Equal(BlueprintRunState.Failed, row.State);
        Assert.Contains("Health", row.FirstError);
    }

    [Fact]
    public void Two_errors_on_one_blueprint_are_one_row()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/BP/BP_Broken.BP_Broken"),
            CompilerError("first"),
            CompilerError("second"),
            Completed(2, 0)
        });

        var row = Assert.Single(report.Results);

        Assert.Equal(2, row.ErrorCount);
    }

    /// <summary>
    /// Start-up is where the DDC, shader and module noise lives. Blaming it on the first
    /// asset would send someone to debug a Blueprint over a missing shader cache.
    /// </summary>
    [Fact]
    public void Errors_before_the_first_blueprint_belong_to_no_blueprint()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            "LogModuleManager: Error: could not load something",
            Loading("/Game/BP/BP_Fine.BP_Fine"),
            Completed(0, 0)
        });

        Assert.Empty(report.Results);
        Assert.Equal(1, report.StartupErrorCount);
    }

    [Fact]
    public void Noise_categories_are_never_attributed()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/BP/BP_Fine.BP_Fine"),
            "LogShaderCompilers: Error: failed to compile a shader",
            "LogDerivedDataCache: Error: cache is unreachable",
            Completed(0, 0)
        });

        Assert.Empty(report.Results);
    }

    /// <summary>
    /// Blueprint compile warnings are ubiquitous. Counting them would make every project
    /// fail on its first run, and the option would be switched off the same afternoon.
    /// </summary>
    [Fact]
    public void Warnings_are_not_failures()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/BP/BP_Fine.BP_Fine"),
            "LogBlueprint: Warning: [Compiler] unused local variable",
            Completed(0, 1)
        });

        Assert.Empty(report.Results);
    }

    /// <summary>The tool's own banners go through the same sink as the engine's output.</summary>
    [Fact]
    public void The_tools_own_lines_are_not_attributed()
    {
        var collector = new BlueprintFailureCollector(BlueprintPassKind.CompileAll);

        collector.Observe(LogParser.Parse(Loading("/Game/BP/BP_Fine.BP_Fine")));
        collector.Observe(new LogEntry { Text = "Step 2 of 3 failed", Severity = LogSeverity.Error, Category = "FMFC" });
        collector.Observe(LogParser.Parse(Completed(0, 0)));

        Assert.Empty(collector.Complete(0).Results);
    }

    /// <summary>
    /// From a real run: a dependency's struct reference broke, and the error surfaced while
    /// an unrelated Blueprint was being compiled. Blaming the one in flight would send
    /// someone to the wrong asset entirely.
    /// </summary>
    [Fact]
    public void A_load_error_goes_to_the_asset_it_names_not_the_one_in_flight()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/AdvancedPlacement/BP_ThirdPersonGameMode.BP_ThirdPersonGameMode"),
            "LogProperty: Error: FStructProperty::Serialize Loading: Property 'StructProperty " +
            "/Game/Gallery/BP/Widget/Pizzeria/WBP_OrderPizzeria.WBP_OrderPizzeria_C:PizzeriaTakingOrderDefinition'. " +
            "Unknown structure.",
            Completed(1, 0)
        });

        var row = Assert.Single(report.Results);

        Assert.Equal("/Game/Gallery/BP/Widget/Pizzeria/WBP_OrderPizzeria", row.Path);
    }

    /// <summary>A compiler error names no asset, so position is all there is to go on.</summary>
    [Fact]
    public void A_compiler_error_still_goes_to_the_blueprint_being_compiled()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/BP/BP_Broken.BP_Broken"),
            CompilerError("Struct member Health not found"),
            Completed(1, 0)
        });

        Assert.Equal("/Game/BP/BP_Broken", Assert.Single(report.Results).Path);
    }

    /// <summary>Listed separately: nobody can fix an engine Blueprint from this page.</summary>
    [Fact]
    public void Engine_content_is_counted_but_not_listed()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Engine/EngineDamageTypes/DmgTypeBP_Environmental.DmgTypeBP_Environmental"),
            CompilerError("engine content is broken"),
            Loading("/Game/BP/BP_Broken.BP_Broken"),
            CompilerError("ours is broken too"),
            Completed(2, 0)
        });

        Assert.Single(report.Results);
        Assert.Equal("/Game/BP/BP_Broken", report.Results[0].Path);
        Assert.Equal(1, report.ForeignFailureCount);
    }

    /// <summary>
    /// The editor died partway. A partial answer must not be handed over as the whole one,
    /// which is why the closing line — not the per-asset ones — is what makes a run count.
    /// </summary>
    [Fact]
    public void Not_conclusive_when_the_commandlet_never_reported_that_it_finished()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/BP/BP_Fine.BP_Fine"),
            Loading("/Game/BP/BP_Broken.BP_Broken"),
            CompilerError("Struct member Health not found")
        });

        Assert.False(report.Conclusive);
        Assert.NotEmpty(report.Diagnosis);
    }

    /// <summary>
    /// The commandlet counted errors and the page could not say whose. Showing an empty
    /// list here is the exact lie this whole class exists to prevent.
    /// </summary>
    /// <remarks>
    /// This is what -ShowResultsOnly would do to a run: the compiler result log goes
    /// silent, the per-Blueprint lines never appear, and the totals are all that is left.
    /// It is why that switch is not passed.
    /// </remarks>
    [Fact]
    public void Errors_the_commandlet_counted_but_we_could_not_attribute_are_diagnosed()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/BP/BP_Fine.BP_Fine"),
            Loading("/Game/BP/BP_Broken.BP_Broken"),
            Completed(4, 0)
        });

        Assert.True(report.Conclusive);
        Assert.Empty(report.Results);
        Assert.Contains("4 error", report.Diagnosis);
    }

    /// <summary>The -SimpleAssetList block, which the diagnosis falls back to naming.</summary>
    [Fact]
    public void The_commandlets_own_asset_list_is_read_and_quoted_back()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/BP/BP_Fine.BP_Fine"),
            Completed(2, 0),
            "LogCompileAllBlueprintsCommandlet: Warning: ",
            "===================================================================================",
            "Assets With Errors or Warnings:",
            "===================================================================================",
            "LogCompileAllBlueprintsCommandlet: Warning: /Game/BP/BP_Broken.BP_Broken",
            "LogCompileAllBlueprintsCommandlet: Warning: /Game/BP/BP_AlsoBroken.BP_AlsoBroken",
            "===================================================================================",
            "End of Asset List",
            "==================================================================================="
        });

        Assert.Contains("/Game/BP/BP_Broken", report.Diagnosis);
        Assert.Contains("/Game/BP/BP_AlsoBroken", report.Diagnosis);
    }

    /// <summary>An asset that never got as far as compiling names itself on its own line.</summary>
    [Fact]
    public void A_blueprint_that_would_not_load_is_reported()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/BP/BP_Corrupt.BP_Corrupt"),
            "LogCompileAllBlueprintsCommandlet: Error: Failed to Load : '/Game/BP/BP_Corrupt.BP_Corrupt'.",
            Completed(0, 0, failedLoads: 1)
        });

        var row = Assert.Single(report.Results);

        Assert.Equal("/Game/BP/BP_Corrupt", row.Path);
    }

    /// <summary>The counts are carried out so the run can report them rather than re-derive them.</summary>
    [Fact]
    public void The_commandlets_own_totals_are_carried_out()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[] { Loading("/Game/BP/BP_Fine.BP_Fine"), Completed(3, 12, 1) });

        Assert.Equal(new BlueprintTotals(3, 12, 1), report.Totals);
    }

    /// <summary>Engine versions before 5.1 ended the sentence at the warning count.</summary>
    [Fact]
    public void An_older_summary_line_without_the_failed_load_clause_still_reads()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            Loading("/Game/BP/BP_Fine.BP_Fine"),
            "Compiling Completed with 2 errors and 5 warnings."
        });

        Assert.True(report.Conclusive);
        Assert.Equal(new BlueprintTotals(2, 5, 0), report.Totals);
    }

    /// <summary>
    /// The original of this pair: output in a shape nobody parsed must never come back as
    /// a clean bill of health.
    /// </summary>
    [Fact]
    public void Not_conclusive_when_the_commandlet_printed_nothing_we_recognise()
    {
        var report = Run(BlueprintPassKind.CompileAll, new[]
        {
            "LogSomethingNew: Display: Compiling 1204 assets in a format nobody parsed",
            "LogSomethingNew: Display: Done."
        });

        Assert.False(report.Conclusive);
        Assert.NotEmpty(report.Diagnosis);

        var row = Assert.Single(report.Results);

        Assert.Equal(BlueprintRunState.Unknown, row.State);
    }

    /// <summary>
    /// A Blueprint failure does not reliably change the exit code, so the exit code can
    /// never be read as "everything compiled" — but a non-zero one with nothing attributed
    /// still has to be said out loud.
    /// </summary>
    [Fact]
    public void A_failing_exit_code_with_nothing_attributed_is_diagnosed()
    {
        var report = Run(
            BlueprintPassKind.CompileAll,
            new[] { Loading("/Game/BP/BP_Fine.BP_Fine"), Completed(0, 0) },
            exitCode: 3);

        Assert.True(report.Conclusive);
        Assert.Empty(report.Results);
        Assert.Contains("3", report.Diagnosis);
    }

    // ---------------------------------------------------------------- refresh step

    private static string Begin(string path) => $"LogPython: Display: {BlueprintBuilder.BeginMarker} {path}";

    private static string End(string path, string result)
        => $"LogPython: Display: {BlueprintBuilder.EndMarker} {path} {result}";

    private static string Summary(int attempted, int saved)
        => $"LogPython: Display: {BlueprintBuilder.SummaryMarker} attempted={attempted} saved={saved}";

    [Fact]
    public void A_blueprint_that_refreshed_cleanly_is_refreshed()
    {
        var report = Run(BlueprintPassKind.Refresh, new[]
        {
            Begin("/Game/BP/BP_Door"),
            End("/Game/BP/BP_Door", BlueprintBuilder.ResultOk),
            Summary(1, 1)
        });

        Assert.True(report.Conclusive);
        Assert.Equal(BlueprintRunState.Reloaded, Assert.Single(report.Results).State);
        Assert.Empty(report.Unresolved);
    }

    /// <summary>
    /// compile_blueprint() returns None, so the script's "OK" only means it did not throw.
    /// Whether the Blueprint still fails is decided here, from the errors in between.
    /// </summary>
    [Fact]
    public void Errors_between_the_markers_outrank_the_scripts_own_ok()
    {
        var report = Run(BlueprintPassKind.Refresh, new[]
        {
            Begin("/Game/BP/BP_Door"),
            CompilerError("still cannot find Health"),
            End("/Game/BP/BP_Door", BlueprintBuilder.ResultOk),
            Summary(1, 1)
        });

        Assert.Equal(BlueprintRunState.StillFailing, Assert.Single(report.Results).State);
    }

    [Fact]
    public void A_save_that_failed_is_its_own_state()
    {
        var report = Run(BlueprintPassKind.Refresh, new[]
        {
            Begin("/Game/BP/BP_Door"),
            End("/Game/BP/BP_Door", BlueprintBuilder.ResultSaveFailed),
            Summary(1, 0)
        });

        Assert.Equal(BlueprintRunState.SaveFailed, Assert.Single(report.Results).State);
    }

    /// <summary>The editor died mid-asset. That Blueprint was not fixed.</summary>
    [Fact]
    public void A_begin_with_no_end_is_unknown_rather_than_fixed()
    {
        var report = Run(BlueprintPassKind.Refresh, new[]
        {
            Begin("/Game/BP/BP_Door"),
            End("/Game/BP/BP_Door", BlueprintBuilder.ResultOk),
            Begin("/Game/BP/BP_Crash"),
            Summary(2, 1)
        });

        var crashed = report.Results.Single(row => row.Path == "/Game/BP/BP_Crash");

        Assert.Equal(BlueprintRunState.Unknown, crashed.State);
        Assert.Single(report.Unresolved);
    }

    [Fact]
    public void No_summary_means_the_script_did_not_finish()
    {
        var report = Run(BlueprintPassKind.Refresh, new[]
        {
            Begin("/Game/BP/BP_Door"),
            End("/Game/BP/BP_Door", BlueprintBuilder.ResultOk)
        });

        Assert.False(report.Conclusive);
        Assert.NotEmpty(report.Diagnosis);
    }

    /// <summary>
    /// The real check on the Python plugin: the .uproject warning is only advisory, since
    /// a plugin can be enabled by a config this tool never reads.
    /// </summary>
    [Fact]
    public void No_python_output_at_all_is_diagnosed_as_a_missing_plugin()
    {
        var report = Run(BlueprintPassKind.Refresh, new[]
        {
            "LogInit: Display: the editor started and then stopped"
        });

        Assert.False(report.Conclusive);
        Assert.Contains("plugin", report.Diagnosis);
    }

    /// <summary>"Refreshed" claims the asset was written. It must not when it wasn't.</summary>
    [Fact]
    public void A_dry_run_does_not_claim_the_asset_was_refreshed_and_saved()
    {
        var report = Run(BlueprintPassKind.Refresh, new[]
        {
            Begin("/Game/BP/BP_Door"),
            End("/Game/BP/BP_Door", BlueprintBuilder.ResultOk),
            Summary(1, 0)
        }, saving: false);

        Assert.Equal(BlueprintRunState.Saved, Assert.Single(report.Results).State);
    }

    // ---------------------------------------------------------------- markers vs the parser

    /// <summary>
    /// LogParser classifies any "Identifier: Verbosity:" pair, so a marker carrying the
    /// word Error would be logged as an error and counted against the Blueprint it names.
    /// </summary>
    [Fact]
    public void No_marker_line_classifies_itself_as_an_error()
    {
        foreach (var line in new[]
                 {
                     Begin("/Game/BP/BP_Door"),
                     End("/Game/BP/BP_Door", BlueprintBuilder.ResultOk),
                     End("/Game/BP/BP_Door", BlueprintBuilder.ResultLoadFailed),
                     End("/Game/BP/BP_Door", BlueprintBuilder.ResultSaveFailed),
                     End("/Game/BP/BP_Door", BlueprintBuilder.ResultException),
                     Summary(1, 1)
                 })
        {
            Assert.Equal(LogSeverity.Info, LogParser.Parse(line).Severity);
        }
    }

    [Fact]
    public void Lines_arriving_after_the_pass_closed_are_ignored()
    {
        var collector = new BlueprintFailureCollector(BlueprintPassKind.CompileAll);

        collector.Observe(LogParser.Parse(Loading("/Game/BP/BP_Fine.BP_Fine")));
        collector.Observe(LogParser.Parse(Completed(0, 0)));

        var report = collector.Complete(0);

        // Unsubscribing does not cancel a handler already in flight.
        collector.Observe(LogParser.Parse(CompilerError("late arrival")));

        Assert.Empty(report.Results);
    }
}
