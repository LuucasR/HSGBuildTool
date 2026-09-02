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

    /// <summary>
    /// Map selection for the Navigation page. Superseded by
    /// <see cref="NavigationPresets"/>; kept so a config written before presets existed
    /// still restores its selection, into the Default preset.
    /// </summary>
    public List<string> NavigationMaps { get; set; } = new();

    /// <summary>Map selection for the Lighting page. Legacy, as above.</summary>
    public List<string> LightingMaps { get; set; } = new();

    public string LightingQuality { get; set; } = "Production";

    public string ActiveNavigationPreset { get; set; } = CommandletPreset.DefaultName;

    public List<CommandletPreset> NavigationPresets { get; set; } = new();

    public string ActiveLightingPreset { get; set; } = CommandletPreset.DefaultName;

    public List<CommandletPreset> LightingPresets { get; set; } = new();

    public BuildPreset GetActivePreset()
    {
        if (Presets.Count == 0)
            Presets.Add(new BuildPreset());

        return Presets.FirstOrDefault(p => p.Name == ActivePreset) ?? Presets[0];
    }

    /// <summary>
    /// The Navigation or Lighting preset list, seeded on first use from the flat
    /// selection those pages used to keep, so upgrading does not lose it.
    /// </summary>
    public List<CommandletPreset> PresetsFor(string kind)
    {
        var presets = kind == "lighting" ? LightingPresets : NavigationPresets;

        if (presets.Count == 0)
        {
            presets.Add(new CommandletPreset
            {
                Maps = (kind == "lighting" ? LightingMaps : NavigationMaps).ToList(),
                Quality = LightingQuality
            });
        }

        return presets;
    }

    public CommandletPreset GetActiveCommandletPreset(string kind)
    {
        var presets = PresetsFor(kind);
        var active = kind == "lighting" ? ActiveLightingPreset : ActiveNavigationPreset;

        return presets.FirstOrDefault(p => p.Name == active) ?? presets[0];
    }

    public void SetActiveCommandletPreset(string kind, string name)
    {
        if (kind == "lighting")
            ActiveLightingPreset = name;
        else
            ActiveNavigationPreset = name;
    }
}
