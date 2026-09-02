using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The problems list: the errors and warnings pulled out of the log so a failed build
/// does not have to be found by scrolling through two hundred thousand lines.
/// </summary>
public class OutputIssuesTests : IDisposable
{
    private readonly string _root;
    private readonly OutputService _output;

    public OutputIssuesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        _output = new OutputService(Path.Combine(_root, "Logs"));
    }

    [Fact]
    public void Collects_only_warnings_and_errors_in_order()
    {
        Run(async view =>
        {
            _output.WriteTool("starting");
            _output.WriteTool("careful", LogSeverity.Warning);
            _output.WriteTool("still going");
            _output.WriteTool("broken", LogSeverity.Error);

            await FlushAsync();

            Assert.Equal(new[] { "careful", "broken" }, view.Issues.Select(i => i.Text));
        });
    }

    /// <summary>
    /// Deliberately independent of the severity chips: turning off Errors to read through
    /// the noise must not also empty the list you use to find your way back.
    /// </summary>
    [Fact]
    public void The_severity_chips_do_not_change_the_problems_list()
    {
        Run(async view =>
        {
            _output.WriteTool("broken", LogSeverity.Error);
            _output.WriteTool("careful", LogSeverity.Warning);

            await FlushAsync();

            view.ShowErrors = false;
            view.ShowWarnings = false;

            await FlushAsync();

            Assert.Equal(2, view.Issues.Count);
            Assert.DoesNotContain(view.Lines, l => l.Severity is LogSeverity.Error or LogSeverity.Warning);
        });
    }

    [Fact]
    public void Next_and_previous_walk_the_list_and_wrap()
    {
        Run(async view =>
        {
            _output.WriteTool("one", LogSeverity.Error);
            _output.WriteTool("two", LogSeverity.Warning);
            _output.WriteTool("three", LogSeverity.Error);

            await FlushAsync();

            view.NextIssueCommand.Execute(null);
            Assert.Equal("one", view.SelectedIssue!.Text);

            view.NextIssueCommand.Execute(null);
            Assert.Equal("two", view.SelectedIssue!.Text);

            view.NextIssueCommand.Execute(null);
            Assert.Equal("three", view.SelectedIssue!.Text);

            // Wraps rather than sticking at the end.
            view.NextIssueCommand.Execute(null);
            Assert.Equal("one", view.SelectedIssue!.Text);

            view.PreviousIssueCommand.Execute(null);
            Assert.Equal("three", view.SelectedIssue!.Text);
        });
    }

    [Fact]
    public void Stepping_opens_the_panel_and_reports_the_position()
    {
        Run(async view =>
        {
            _output.WriteTool("one", LogSeverity.Error);
            _output.WriteTool("two", LogSeverity.Error);

            await FlushAsync();

            Assert.False(view.ShowIssues);

            view.NextIssueCommand.Execute(null);

            // Jumping with the list hidden leaves no sense of how many more there are.
            Assert.True(view.ShowIssues);
            Assert.Equal("1 of 2", view.IssuePositionText);

            view.NextIssueCommand.Execute(null);

            Assert.Equal("2 of 2", view.IssuePositionText);
        });
    }

    /// <summary>
    /// Jumping to a line the current filter hides would silently do nothing, so selecting
    /// an issue turns its severity back on.
    /// </summary>
    [Fact]
    public void Selecting_an_issue_re_enables_the_filter_that_hides_it()
    {
        Run(async view =>
        {
            _output.WriteTool("broken", LogSeverity.Error);

            await FlushAsync();

            view.ShowErrors = false;

            await FlushAsync();

            view.SelectedIssue = view.Issues[0];

            Assert.True(view.ShowErrors);
        });
    }

    /// <summary>Following the tail would drag the view straight back off the line jumped to.</summary>
    [Fact]
    public void Jumping_to_an_issue_stops_auto_scroll()
    {
        Run(async view =>
        {
            _output.WriteTool("broken", LogSeverity.Error);

            await FlushAsync();

            Assert.True(view.AutoScroll);

            view.SelectedIssue = view.Issues[0];

            Assert.False(view.AutoScroll);
        });
    }

    [Fact]
    public void Clearing_the_log_clears_the_problems_list()
    {
        Run(async view =>
        {
            _output.WriteTool("broken", LogSeverity.Error);

            await FlushAsync();

            Assert.Single(view.Issues);

            view.ClearCommand.Execute(null);

            await FlushAsync();

            Assert.Empty(view.Issues);
            Assert.Null(view.SelectedIssue);
        });
    }

    [Fact]
    public void Stepping_does_nothing_with_a_clean_log()
    {
        Run(async view =>
        {
            _output.WriteTool("all fine");

            await FlushAsync();

            Assert.False(view.NextIssueCommand.CanExecute(null));

            view.NextIssueCommand.Execute(null);

            Assert.Null(view.SelectedIssue);
            Assert.Equal("", view.IssuePositionText);
        });
    }

    private void Run(Func<OutputViewModel, Task> body)
        => Sta.Run(() => body(new OutputViewModel(_output, new AppConfig())));

    /// <summary>
    /// The view-model batches incoming lines and flushes on a 100 ms timer, so a test has
    /// to let the dispatcher run before asserting.
    /// </summary>
    private static async Task FlushAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);

        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(25);
        }
    }

    public void Dispose()
    {
        _output.Dispose();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
