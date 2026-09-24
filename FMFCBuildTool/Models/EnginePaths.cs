namespace FMFCBuildTool.Models;

/// <summary>
/// A resolved Unreal Engine installation. Single source of truth for every page —
/// Package and Navigation previously resolved the engine through two different,
/// mutually inconsistent mechanisms.
/// </summary>
public sealed class EnginePaths
{
    public required string Root { get; init; }
    public required string RunUAT { get; init; }
    public required string EditorCmd { get; init; }

    /// <summary>Engine\Build\BatchFiles\Build.bat — UnrealBuildTool's entry point, used by the Compiler page.</summary>
    public required string BuildBat { get; init; }

    /// <summary>
    /// Engine\Binaries\Win64\UnrealEditor.exe — the GUI editor, used by the Launch page to run
    /// the game with -game or -server. Derived rather than resolved: it is not part of what
    /// makes a folder an engine, and the Launch page says so itself when it is missing.
    /// </summary>
    public string Editor => System.IO.Path.Combine(Root, @"Engine\Binaries\Win64\UnrealEditor.exe");

    /// <summary>Display version, e.g. "5.4". "Source" for GUID-associated source builds.</summary>
    public string Version { get; init; } = "";

    /// <summary>How this installation was found — shown in Settings so the choice is auditable.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// True for a binary/Launcher engine (Engine\Build\InstalledBuild.txt is present).
    /// Drives UAT's -installed flag, which used to be passed unconditionally and was
    /// therefore wrong for source builds.
    /// </summary>
    public bool IsInstalled { get; init; }

    public override string ToString() => string.IsNullOrEmpty(Version) ? Root : $"UE {Version}";
}
