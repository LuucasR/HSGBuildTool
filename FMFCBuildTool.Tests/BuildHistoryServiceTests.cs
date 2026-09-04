using System;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

public class BuildHistoryServiceTests
{
    private const string Project = @"C:\Games\FMFC\FMFC.uproject";
    private const string Other = @"C:\Games\Other\Other.uproject";

    /// <summary>
    /// The History grid renders KindLabel, so a kind missing from that switch shows up as
    /// the raw key. Cheap to get wrong when a page is added, and invisible until you look.
    /// </summary>
    [Theory]
    [InlineData("compile", "Compiler")]
    [InlineData("package", "Package")]
    [InlineData("nav", "Navigation")]
    [InlineData("lighting", "Lighting")]
    public void Every_kind_has_a_readable_label(string kind, string label)
    {
        Assert.Equal(label, new BuildRecord { Kind = kind }.KindLabel);
    }

    [Fact]
    public void Records_newest_first()
    {
        var (config, history) = Create();

        history.Record(Record("package", seconds: 10));
        history.Record(Record("nav", seconds: 20));

        Assert.Equal(new[] { "nav", "package" }, config.History.Select(r => r.Kind));
    }

    /// <summary>
    /// config.json is rewritten whole on every save, so history cannot be allowed to grow
    /// without bound.
    /// </summary>
    [Fact]
    public void Caps_the_stored_history()
    {
        var (config, history) = Create();

        for (var i = 0; i < BuildHistoryService.MaxRecords + 25; i++)
            history.Record(Record("package", seconds: i + 1));

        Assert.Equal(BuildHistoryService.MaxRecords, config.History.Count);

        // The cap drops the oldest, not the newest.
        Assert.Equal(BuildHistoryService.MaxRecords + 25, config.History[0].DurationSeconds);
    }

    [Fact]
    public void Estimates_from_the_median_of_recent_successful_runs()
    {
        var (_, history) = Create();

        foreach (var seconds in new[] { 100, 110, 120, 130, 140 })
            history.Record(Record("package", seconds));

        Assert.Equal(TimeSpan.FromSeconds(120), history.Estimate("package", Project));
    }

    /// <summary>
    /// A median rather than a mean, so one cold cook that took ten times as long does not
    /// poison the estimate for every run after it.
    /// </summary>
    [Fact]
    public void One_outlier_does_not_move_the_estimate()
    {
        var (_, history) = Create();

        foreach (var seconds in new[] { 100, 110, 120, 130, 5000 })
            history.Record(Record("package", seconds));

        Assert.Equal(TimeSpan.FromSeconds(120), history.Estimate("package", Project));
    }

    /// <summary>A build that failed after ten seconds says nothing about how long one takes.</summary>
    [Fact]
    public void Failed_and_stopped_runs_do_not_count_towards_the_estimate()
    {
        var (_, history) = Create();

        history.Record(Record("package", 300));
        history.Record(Record("package", 4, BuildOutcome.Failed));
        history.Record(Record("package", 7, BuildOutcome.Stopped));

        Assert.Equal(TimeSpan.FromSeconds(300), history.Estimate("package", Project));
    }

    [Fact]
    public void Estimates_are_per_kind_and_per_project()
    {
        var (_, history) = Create();

        history.Record(Record("package", 300));
        history.Record(Record("nav", 30));
        history.Record(Record("package", 900, project: Other));

        Assert.Equal(TimeSpan.FromSeconds(300), history.Estimate("package", Project));
        Assert.Equal(TimeSpan.FromSeconds(30), history.Estimate("nav", Project));
        Assert.Equal(TimeSpan.FromSeconds(900), history.Estimate("package", Other));
        Assert.Null(history.Estimate("lighting", Project));
    }

    [Fact]
    public void Has_no_estimate_before_the_first_successful_run()
    {
        var (_, history) = Create();

        Assert.Null(history.Estimate("package", Project));
    }

    [Fact]
    public void Filters_by_project()
    {
        var (_, history) = Create();

        history.Record(Record("package", 10));
        history.Record(Record("package", 10, project: Other));

        Assert.Single(history.For(Project));
        Assert.Equal(2, history.Records.Count);
    }

    [Fact]
    public void Raises_recorded_so_the_shell_can_notify()
    {
        var (_, history) = Create();

        BuildRecord? seen = null;

        history.Recorded += r => seen = r;
        history.Record(Record("nav", 5, BuildOutcome.Failed));

        Assert.NotNull(seen);
        Assert.Equal(BuildOutcome.Failed, seen!.Outcome);
    }

    private static (AppConfig Config, BuildHistoryService History) Create()
    {
        var config = new AppConfig();

        return (config, new BuildHistoryService(config));
    }

    private static BuildRecord Record(
        string kind,
        double seconds,
        BuildOutcome outcome = BuildOutcome.Succeeded,
        string project = Project)
        => new()
        {
            Kind = kind,
            ProjectFile = project,
            DurationSeconds = seconds,
            Outcome = outcome,
            StartedAt = DateTime.Now
        };
}
