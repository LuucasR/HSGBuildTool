using System.Collections.Generic;
using System.Text.Json;

namespace FMFCBuildTool.Models;

/// <summary>
/// A named map selection for the Navigation, Lighting or HLOD page, stored per .uproject.
/// </summary>
/// <remarks>
/// Package has had named presets from the start; the commandlet pages had a single
/// implicit selection, so "the four combat maps" and "everything" could not both be kept.
/// One type covers all three pages and each ignores the others' options:
/// <see cref="Quality"/> is Lighting's, the Hlod* properties are HLOD's.
/// </remarks>
public class CommandletPreset
{
    public const string DefaultName = "Default";

    /// <summary>Default -Builder= value for HLOD. The literal, not a reference to the
    /// service, so the config model stays free of the command-line layer.</summary>
    public const string DefaultHlodBuilder = "SimplygonWorldPartitionBuilder";

    public string Name { get; set; } = DefaultName;

    /// <summary>Package paths (/Game/Maps/Foo) of the selected maps.</summary>
    public List<string> Maps { get; set; } = new();

    /// <summary>Lighting quality. Unused by Navigation and HLOD.</summary>
    public string Quality { get; set; } = "Production";

    // ------------------------------------------------------------------ HLOD

    /// <summary>"SimplygonWorldPartitionBuilder" or "WorldPartitionHLODsBuilder".</summary>
    public string HlodBuilder { get; set; } = DefaultHlodBuilder;

    /// <summary>Clear the existing HLODs in a first pass, before building.</summary>
    public bool DeleteHlods { get; set; } = true;

    public bool SetupHlods { get; set; } = true;

    public bool BuildHlods { get; set; } = true;

    public bool AllowCommandletRendering { get; set; } = true;

    public bool WaitMutex { get; set; } = true;

    public bool Unattended { get; set; } = true;

    /// <summary>Appended to every HLOD invocation verbatim, e.g. "-DistributedBuild".</summary>
    public string ExtraArguments { get; set; } = "";

    public CommandletPreset Clone()
    {
        var json = JsonSerializer.Serialize(this);

        return JsonSerializer.Deserialize<CommandletPreset>(json) ?? new CommandletPreset();
    }
}
