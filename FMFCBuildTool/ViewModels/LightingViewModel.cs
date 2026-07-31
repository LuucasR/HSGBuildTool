using System.Collections.Generic;
using System.Linq;
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

    public LightingViewModel(BuildContext context, ProcessRunner runner, OutputService output, AppConfig config)
        : base(context, runner, output, config)
    {
    }

    public override string RunButtonText => "BUILD LIGHTING";

    public IReadOnlyList<string> Qualities => LightingBuilder.Qualities;

    public string Quality
    {
        get => _quality;
        set
        {
            if (!SetProperty(ref _quality, value))
                return;

            Settings.LightingQuality = value;

            Refresh();
        }
    }

    protected override string SessionLabel => "lighting";

    protected override string ActionName => "Lighting build";

    protected override IReadOnlyList<string> ArgumentsFor(string map)
        => LightingBuilder.BuildArguments(Context.ProjectFile, map, Quality);

    protected override IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps)
        => LightingBuilder.Validate(Context.ProjectFile, maps, Quality);

    protected override IReadOnlyList<string> ReadSavedSelection(ProjectSettings settings)
    {
        // Restore the saved quality alongside the map selection.
        if (LightingBuilder.Qualities.Contains(settings.LightingQuality))
            SetProperty(ref _quality, settings.LightingQuality, nameof(Quality));

        return settings.LightingMaps;
    }

    protected override void WriteSelection(ProjectSettings settings, IReadOnlyList<string> maps)
        => settings.LightingMaps = maps.ToList();
}
