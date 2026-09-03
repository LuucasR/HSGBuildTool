namespace FMFCBuildTool.Models;

/// <summary>
/// Which severities a log export keeps.
/// </summary>
/// <remarks>
/// Reading a failed cook means finding a few dozen lines in a couple of hundred thousand,
/// and the only way to hand those to someone else was to copy the whole file and prune it
/// by hand. The severity chips already answer this question on screen; this is the same
/// question asked of a file.
///
/// <see cref="LogSeverity.Verbose"/> rides along with Info, matching
/// <c>OutputViewModel.Passes</c>, where the "All" chip covers both.
/// </remarks>
public enum LogExportScope
{
    Everything,
    WarningsAndErrors,
    ErrorsOnly,
    WarningsOnly
}

public static class LogExportScopeExtensions
{
    public static bool Includes(this LogExportScope scope, LogSeverity severity) => scope switch
    {
        LogExportScope.Everything => true,
        LogExportScope.WarningsAndErrors => severity is LogSeverity.Warning or LogSeverity.Error,
        LogExportScope.ErrorsOnly => severity == LogSeverity.Error,
        LogExportScope.WarningsOnly => severity == LogSeverity.Warning,
        _ => true
    };

    /// <summary>Suffix appended to the suggested file name, so exports do not overwrite each other.</summary>
    public static string FileSuffix(this LogExportScope scope) => scope switch
    {
        LogExportScope.WarningsAndErrors => "issues",
        LogExportScope.ErrorsOnly => "errors",
        LogExportScope.WarningsOnly => "warnings",
        _ => "full"
    };
}
