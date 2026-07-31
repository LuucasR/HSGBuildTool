using System.Collections.Generic;
using System.Text.Json;

namespace FMFCBuildTool.Models;

/// <summary>
/// A named set of BuildCookRun options, stored per .uproject.
/// </summary>
/// <remarks>
/// Deliberately holds no resolved engine paths (RunUAT, UnrealEditor-Cmd). The old
/// BuildConfiguration persisted them, so a preset saved before an engine upgrade
/// would point at a path that no longer existed. They are now resolved from the
/// project at build time by <see cref="Services.UnrealLocator"/>.
/// </remarks>
public class BuildPreset
{
    public const string DefaultName = "Default";

    public string Name { get; set; } = DefaultName;

    // ---- Target ----
    public string Platform { get; set; } = "Win64";
    public string Configuration { get; set; } = "Shipping";
    public bool Client { get; set; }
    public bool Server { get; set; }

    // ---- Pipeline ----
    public bool Build { get; set; } = true;
    public bool Cook { get; set; } = true;
    public bool Stage { get; set; } = true;
    public bool Package { get; set; } = true;
    public bool Archive { get; set; } = true;
    public string ArchiveDirectory { get; set; } = "";

    // ---- Cook ----
    public bool FullCook { get; set; }
    public bool CookIncremental { get; set; }
    public bool ZenStore { get; set; }
    public bool SkipCookingEditorContent { get; set; } = true;
    public bool UnversionedCookedContent { get; set; } = true;
    public List<string> CookCultures { get; set; } = new() { "en" };

    // ---- Packaging ----
    public bool Pak { get; set; } = true;
    public bool IoStore { get; set; } = true;
    public bool Compressed { get; set; } = true;
    public bool Prereqs { get; set; }
    public bool Distribution { get; set; }
    public bool CrashReporter { get; set; }

    // ---- Advanced / logging ----
    public bool NoCompile { get; set; } = true;
    public bool NoCompileEditor { get; set; } = true;
    public bool FileOpenLog { get; set; } = true;
    public bool StdOut { get; set; } = true;
    public bool CrashForUAT { get; set; } = true;
    public bool Unattended { get; set; } = true;
    public bool NoLogTimes { get; set; } = true;

    // ---- Maps ----
    public bool UseProjectDefaultMaps { get; set; } = true;

    /// <summary>
    /// Package paths (/Game/Maps/Foo) of the maps to cook.
    /// Must keep its setter: as a get-only property System.Text.Json serialised it
    /// but silently refused to repopulate it, so every saved selection was lost on load.
    /// </summary>
    public List<string> Maps { get; set; } = new();

    /// <summary>
    /// Deep copy via a JSON round-trip rather than a hand-written member-by-member
    /// clone, so adding an option here cannot silently fall out of the copy.
    /// </summary>
    public BuildPreset Clone()
    {
        var json = JsonSerializer.Serialize(this);

        return JsonSerializer.Deserialize<BuildPreset>(json) ?? new BuildPreset();
    }
}
