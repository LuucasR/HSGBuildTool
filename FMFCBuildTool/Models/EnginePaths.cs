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
