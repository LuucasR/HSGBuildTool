using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// Regression cover for the log classification that used to be substring matching.
/// </summary>
public class LogParserTests
{
    [Theory]
    [InlineData("LogCook: Error: Failed to cook /Game/Maps/L_Arena")]
    [InlineData("PackagingResults: Error: Unknown Error")]
    [InlineData("LogInit: Fatal: Assertion failed")]
    [InlineData("[2025.07.31-14.22.01:337][  0]LogCook: Error: boom")]
    [InlineData("UATHelper: Packaging (Windows): LogCook: Error: boom")]
    [InlineData("ERROR: AutomationTool terminated with exception")]
    [InlineData("D:\\Proj\\Foo.cpp(120): error C2065: undeclared identifier")]
    public void Recognises_errors(string line)
    {
        Assert.Equal(LogSeverity.Error, LogParser.Parse(line).Severity);
    }

    [Theory]
    [InlineData("LogCook: Warning: Missing reference")]
    [InlineData("WARNING: Deprecated flag")]
    [InlineData("D:\\Proj\\Foo.cs(3,5): warning CS0168: unused variable")]
    public void Recognises_warnings(string line)
    {
        Assert.Equal(LogSeverity.Warning, LogParser.Parse(line).Severity);
    }

    /// <summary>
    /// The old IsError matched "automationtool exiting with exitcode", which is also how
    /// a successful build ends, so every green build finished with a red line.
    /// </summary>
    [Fact]
    public void Successful_exit_is_not_an_error()
    {
        var entry = LogParser.Parse("AutomationTool exiting with ExitCode=0 (Success)");

        Assert.Equal(LogSeverity.Info, entry.Severity);
    }

    [Fact]
    public void Failing_exit_is_an_error()
    {
        var entry = LogParser.Parse("AutomationTool exiting with ExitCode=1 (Error_Unknown)");

        Assert.Equal(LogSeverity.Error, entry.Severity);
    }

    /// <summary>The old IsWarning was line.Contains("warning") — any such path tripped it.</summary>
    [Theory]
    [InlineData("LogCook: Display: Cooking /Game/Maps/L_Warning")]
    [InlineData("LogInit: Display: -Wno-warning-flag passed to the compiler")]
    public void Word_warning_in_a_path_is_not_a_warning(string line)
    {
        Assert.Equal(LogSeverity.Info, LogParser.Parse(line).Severity);
    }

    /// <summary>The old IsError also matched a bare "exception" anywhere in the line.</summary>
    [Fact]
    public void Word_exception_alone_is_not_an_error()
    {
        var entry = LogParser.Parse("LogInit: Display: Registering exception handler");

        Assert.Equal(LogSeverity.Info, entry.Severity);
    }

    [Fact]
    public void Extracts_the_category()
    {
        Assert.Equal("LogCook", LogParser.Parse("LogCook: Warning: something").Category);
    }

    [Fact]
    public void Plain_text_is_info()
    {
        Assert.Equal(LogSeverity.Info, LogParser.Parse("Building 3 of 8").Severity);
    }
}
