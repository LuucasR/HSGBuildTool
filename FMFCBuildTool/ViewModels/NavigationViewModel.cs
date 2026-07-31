using System.Collections.Generic;
using System.Linq;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// Builds World Partition navigation data for the selected maps, one
/// UnrealEditor-Cmd invocation per map.
/// </summary>
public sealed class NavigationViewModel : CommandletPageViewModel
{
    public NavigationViewModel(BuildContext context, ProcessRunner runner, OutputService output, AppConfig config)
        : base(context, runner, output, config)
    {
    }

    public override string RunButtonText => "BUILD NAVIGATION";

    protected override string SessionLabel => "nav";

    protected override string ActionName => "Navigation build";

    protected override IReadOnlyList<string> ArgumentsFor(string map)
        => NavigationBuilder.BuildArguments(Context.ProjectFile, map);

    protected override IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps)
        => NavigationBuilder.Validate(Context.ProjectFile, maps);

    protected override IReadOnlyList<string> ReadSavedSelection(ProjectSettings settings)
        => settings.NavigationMaps;

    protected override void WriteSelection(ProjectSettings settings, IReadOnlyList<string> maps)
        => settings.NavigationMaps = maps.ToList();
}
