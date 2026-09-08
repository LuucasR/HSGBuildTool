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

    public string ActiveHlodPreset { get; set; } = CommandletPreset.DefaultName;

    public List<CommandletPreset> HlodPresets { get; set; } = new();

    /// <summary>
    /// The Compiler page's target, e.g. "MyProjectEditor". Two plain properties rather than
    /// a preset list: a compile is a target and a configuration, and there is nothing else
    /// to name and save.
    /// </summary>
    public string CompileTarget { get; set; } = "";

    /// <summary>"Debug", "Development" or "Shipping" as the page shows them.</summary>
    public string CompileConfiguration { get; set; } = "Development";

    public BuildPreset GetActivePreset()
    {
        if (Presets.Count == 0)
            Presets.Add(new BuildPreset());

        return Presets.FirstOrDefault(p => p.Name == ActivePreset) ?? Presets[0];
    }

    /// <summary>
    /// The Navigation, Lighting or HLOD preset list, seeded on first use from the flat
    /// selection those pages used to keep, so upgrading does not lose it. HLOD is newer
    /// than presets and has no flat selection to inherit, so its Default starts empty
    /// rather than borrowing Navigation's maps.
    /// </summary>
    public List<CommandletPreset> PresetsFor(string kind)
    {
        var presets = kind switch
        {
            "lighting" => LightingPresets,
            "hlod" => HlodPresets,
            _ => NavigationPresets
        };

        if (presets.Count == 0)
        {
            presets.Add(new CommandletPreset
            {
                Maps = kind switch
                {
                    "lighting" => LightingMaps.ToList(),
                    "hlod" => new List<string>(),
                    _ => NavigationMaps.ToList()
                },
                Quality = LightingQuality
            });
        }

        return presets;
    }

    public CommandletPreset GetActiveCommandletPreset(string kind)
    {
        var presets = PresetsFor(kind);

        var active = kind switch
        {
            "lighting" => ActiveLightingPreset,
            "hlod" => ActiveHlodPreset,
            _ => ActiveNavigationPreset
        };

        return presets.FirstOrDefault(p => p.Name == active) ?? presets[0];
    }

    public void SetActiveCommandletPreset(string kind, string name)
    {
        switch (kind)
        {
            case "lighting":
                ActiveLightingPreset = name;
                break;

            case "hlod":
                ActiveHlodPreset = name;
                break;

            default:
                ActiveNavigationPreset = name;
                break;
        }
    }
}
