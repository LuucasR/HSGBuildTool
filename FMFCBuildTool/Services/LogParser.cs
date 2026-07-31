using System;
using System.Text.RegularExpressions;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>
/// Classifies a line of engine/UAT output into a <see cref="LogEntry"/>.
/// </summary>
/// <remarks>
/// Replaces the substring matching that used to decide colour and filtering:
/// <c>line.Contains("warning")</c> flagged any path containing the word, and the
/// error test matched "automationtool exiting with exitcode" — which is also how a
/// <em>successful</em> build ends, so every green build finished with a red line.
/// This parses Unreal's actual "Category: Verbosity: message" structure and only
/// falls back to heuristics for lines that don't have it.
/// </remarks>
public static partial class LogParser
{
    public static LogEntry Parse(string line)
    {
        if (string.IsNullOrEmpty(line))
            return new LogEntry { Text = line ?? "", Severity = LogSeverity.Info };

        var body = TimestampPrefix().Replace(line, "");

        // ---- Unreal's structured form: "LogCook: Warning: message" ----
        var structured = CategoryVerbosity().Match(body);

        if (structured.Success)
        {
            var category = structured.Groups["cat"].Value;
            var severity = structured.Groups["verb"].Value.ToLowerInvariant() switch
            {
                "fatal" or "error" => LogSeverity.Error,
                "warning" => LogSeverity.Warning,
                "verbose" or "veryverbose" => LogSeverity.Verbose,
                _ => LogSeverity.Info
            };

            return new LogEntry { Text = line, Severity = severity, Category = category };
        }

        // ---- UAT's own summary line. ExitCode=0 is a success, not an error. ----
        var exit = AutomationToolExit().Match(body);

        if (exit.Success)
        {
            var failed = exit.Groups["code"].Value != "0";

            return new LogEntry
            {
                Text = line,
                Severity = failed ? LogSeverity.Error : LogSeverity.Info,
                Category = "AutomationTool"
            };
        }

        // ---- Bare "ERROR:" / "WARNING:" prefixes used by UAT and UBT ----
        var bare = BarePrefix().Match(body);

        if (bare.Success)
        {
            var severity = bare.Groups["verb"].Value.ToLowerInvariant() switch
            {
                "warning" => LogSeverity.Warning,
                _ => LogSeverity.Error
            };

            return new LogEntry { Text = line, Severity = severity };
        }

        // ---- MSVC / MSBuild diagnostics: "Foo.cpp(12): error C2065: ..." ----
        var compiler = CompilerDiagnostic().Match(body);

        if (compiler.Success)
        {
            var severity = compiler.Groups["verb"].Value.Equals("warning", StringComparison.OrdinalIgnoreCase)
                ? LogSeverity.Warning
                : LogSeverity.Error;

            return new LogEntry { Text = line, Severity = severity, Category = "Compiler" };
        }

        if (BuildFailed().IsMatch(body))
            return new LogEntry { Text = line, Severity = LogSeverity.Error };

        return new LogEntry { Text = line, Severity = LogSeverity.Info };
    }

    /// <summary>"[2025.07.31-14.22.01:337][  0]" written when -NoLogTimes is off.</summary>
    [GeneratedRegex(@"^\[[\d.\-:]+\](?:\[\s*\d+\])?\s*")]
    private static partial Regex TimestampPrefix();

    /// <summary>
    /// First "Identifier: Verbosity:" pair. Taking the first (not the last) match means
    /// a wrapper prefix such as "UATHelper: Packaging (Windows): LogCook: Warning:"
    /// still resolves to LogCook/Warning, while an error string quoted inside a Display
    /// message doesn't promote the whole line to an error.
    /// </summary>
    [GeneratedRegex(@"\b(?<cat>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<verb>Fatal|Error|Warning|Display|Log|Verbose|VeryVerbose)\s*:",
        RegexOptions.ExplicitCapture)]
    private static partial Regex CategoryVerbosity();

    [GeneratedRegex(@"AutomationTool\s+exiting\s+with\s+ExitCode\s*=\s*(?<code>-?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AutomationToolExit();

    [GeneratedRegex(@"^\s*(?<verb>ERROR|WARNING|FATAL)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex BarePrefix();

    /// <summary>"Foo.cpp(12): error C2065:" / "Foo.cs(3,5): warning CS0168:"</summary>
    [GeneratedRegex(@"\)\s*:\s*(?<verb>fatal error|error|warning)\s+[A-Za-z]{1,4}\d{2,5}\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerDiagnostic();

    [GeneratedRegex(@"^\s*(BUILD FAILED|Took \d.*BUILD FAILED)", RegexOptions.IgnoreCase)]
    private static partial Regex BuildFailed();
}
