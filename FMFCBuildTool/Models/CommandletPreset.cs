using System.Collections.Generic;
using System.Text.Json;

namespace FMFCBuildTool.Models;

/// <summary>
/// A named map selection for the Navigation or Lighting page, stored per .uproject.
/// </summary>
/// <remarks>
/// Package has had named presets from the start; the commandlet pages had a single
/// implicit selection, so "the four combat maps" and "everything" could not both be kept.
/// <see cref="Quality"/> is only meaningful for Lighting and is ignored by Navigation.
/// </remarks>
public class CommandletPreset
{
    public const string DefaultName = "Default";

    public string Name { get; set; } = DefaultName;

    /// <summary>Package paths (/Game/Maps/Foo) of the selected maps.</summary>
    public List<string> Maps { get; set; } = new();

    /// <summary>Lighting quality. Unused by Navigation.</summary>
    public string Quality { get; set; } = "Production";

    public CommandletPreset Clone()
    {
        var json = JsonSerializer.Serialize(this);

        return JsonSerializer.Deserialize<CommandletPreset>(json) ?? new CommandletPreset();
    }
}
