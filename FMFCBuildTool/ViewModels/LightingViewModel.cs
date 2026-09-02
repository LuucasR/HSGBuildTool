using System.Collections.Generic;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// Rebuilds static lighting for the selected maps, one invocation per map.
/// </summary>
/// <remarks>
/// Only meaningful for projects that bake lighting; on a Lumen-only project the
/// commandlet finishes immediately with nothing to do. The page says so rather than
/// leaving the user to wonder why the run took two seconds.
/// </remarks>
public sealed class LightingViewModel : CommandletPageViewModel
{
    private string _quality = "Production";

    public LightingViewModel(
        BuildContext context,
        ProcessRunner runner,
        OutputService output,
        AppConfig config,
        BuildHistoryService history)
        : base(context, runner, output, config, history)
    {
    }

    public override string Kind => "lighting";

    public override string RunButtonText => "BUILD LIGHTING";

    public IReadOnlyList<string> Qualities => LightingBuilder.Qualities;

    /// <summary>Part of the preset: "Preview for iteration" and "Production" are two presets.</summary>
    public string Quality
    {
        get => _quality;
        set
        {
            if (!SetProperty(ref _quality, value))
                return;

            ActivePreset.Quality = value;

            // Also kept on the settings for the pre-preset config format.
            Settings.LightingQuality = value;

            Refresh();
        }
    }

    protected override string ActionName => "Lighting build";

    protected override string HistoryDetail => Quality;

    protected override IReadOnlyList<string> ArgumentsFor(string map)
        => LightingBuilder.BuildArguments(Context.ProjectFile, map, Quality);

    protected override IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps)
        => LightingBuilder.Validate(Context.ProjectFile, maps, Quality);

    protected override void OnPresetApplied(CommandletPreset preset)
    {
        if (LightingBuilder.Qualities.Contains(preset.Quality))
            SetProperty(ref _quality, preset.Quality, nameof(Quality));
    }

    protected override void CaptureIntoPreset(CommandletPreset preset)
    {
        preset.Quality = Quality;
    }
}
