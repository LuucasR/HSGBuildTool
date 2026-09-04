using System;
using System.IO;
using System.Threading.Tasks;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The three build pages render through one shared BuildActionBar, which binds by name.
/// A missing or misnamed member fails silently at runtime as a blank button, so the
/// contract is asserted here instead.
/// </summary>
public class BuildPageContractTests : IDisposable
{
    private readonly string _root;
    private readonly OutputService _output;

    public BuildPageContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "FMFCBuildToolTests", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_root);

        _output = new OutputService(Path.Combine(_root, "Logs"));
    }

    public static TheoryData<string> PageNames => new() { "Package", "Navigation", "Lighting", "Compiler" };

    [Theory]
    [MemberData(nameof(PageNames))]
    public void Every_page_satisfies_the_action_bar_contract(string page)
    {
        Sta.Run(() =>
        {
            var subject = Create(page);

            Assert.False(string.IsNullOrWhiteSpace(subject.RunButtonText));

            Assert.NotNull(subject.RunCommand);
            Assert.NotNull(subject.StopCommand);
            Assert.NotNull(subject.CopyCommandLineCommand);
            Assert.NotNull(subject.OpenLogFileCommand);
            Assert.NotNull(subject.OpenLogFolderCommand);

            Assert.NotNull(subject.LogExport);
            Assert.NotEmpty(subject.LogExport.Options);

            Assert.NotNull(subject.StatusText);
            Assert.NotNull(subject.ElapsedText);
            Assert.NotNull(subject.CommandPreview);
            Assert.NotNull(subject.ValidationMessage);

            return Task.CompletedTask;
        });
    }

    /// <summary>Stop is dead until something is running, on all three pages.</summary>
    [Theory]
    [MemberData(nameof(PageNames))]
    public void Stop_is_disabled_while_idle(string page)
    {
        Sta.Run(() =>
        {
            var subject = Create(page);

            Assert.False(subject.IsRunning);
            Assert.False(subject.StopCommand.CanExecute(null));

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Package and Compiler each run one opaque external process; the commandlet pages
    /// count maps and can report real progress. The bar picks its mode from this.
    /// </summary>
    [Fact]
    public void Only_single_process_pages_report_indeterminate_progress()
    {
        Sta.Run(() =>
        {
            Assert.True(Create("Package").IsProgressIndeterminate);
            Assert.True(Create("Compiler").IsProgressIndeterminate);
            Assert.False(Create("Navigation").IsProgressIndeterminate);
            Assert.False(Create("Lighting").IsProgressIndeterminate);

            return Task.CompletedTask;
        });
    }

    private IBuildPage Create(string page)
    {
        var config = new AppConfig();
        var context = new BuildContext();
        var runner = new ProcessRunner();

        return page switch
        {
            "Compiler" => new CompilerViewModel(context, runner, _output, config, new BuildHistoryService(config)),
            "Navigation" => new NavigationViewModel(context, runner, _output, config, new BuildHistoryService(config)),
            "Lighting" => new LightingViewModel(context, runner, _output, config, new BuildHistoryService(config)),
            _ => new PackageViewModel(context, runner, _output, config, new BuildHistoryService(config))
        };
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
