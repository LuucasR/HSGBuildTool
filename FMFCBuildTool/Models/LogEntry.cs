namespace FMFCBuildTool.Models;

public enum LogSeverity
{
    Verbose,
    Info,
    Warning,
    Error
}

/// <summary>
/// One parsed line of engine output. Severity is decided once, by
/// <see cref="Services.LogParser"/>, instead of being re-guessed with
/// substring matching every time a line is rendered or filtered.
/// </summary>
public sealed class LogEntry
{
    public required string Text { get; init; }

    public LogSeverity Severity { get; init; } = LogSeverity.Info;

    /// <summary>Unreal log category, e.g. "LogCook". Empty for lines that have none.</summary>
    public string Category { get; init; } = "";
}
