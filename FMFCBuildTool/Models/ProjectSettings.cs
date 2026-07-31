using System.Collections.Generic;
using System.Linq;

namespace FMFCBuildTool.Models;

/// <summary>
/// Everything remembered about one .uproject. Replaces the single
/// BuildConfiguration that used to be stored per project path.
/// </summary>
public class ProjectSettings
{
    public string ActivePreset { get; set; } = BuildPreset.DefaultName;

    public List<BuildPreset> Presets { get; set; } = new();

    /// <summary>Map selection for the Navigation page (independent of the package preset).</summary>
    public List<string> NavigationMaps { get; set; } = new();

    /// <summary>Map selection for the Lighting page.</summary>
    public List<string> LightingMaps { get; set; } = new();

    public string LightingQuality { get; set; } = "Production";

    public BuildPreset GetActivePreset()
    {
        if (Presets.Count == 0)
            Presets.Add(new BuildPreset());

        return Presets.FirstOrDefault(p => p.Name == ActivePreset) ?? Presets[0];
    }
}
