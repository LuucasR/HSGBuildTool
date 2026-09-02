using System.Collections.Generic;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// Builds World Partition navigation data for the selected maps, one
/// UnrealEditor-Cmd invocation per map.
/// </summary>
public sealed class NavigationViewModel : CommandletPageViewModel
{
    public NavigationViewModel(
        BuildContext context,
        ProcessRunner runner,
        OutputService output,
        AppConfig config,
        BuildHistoryService history)
        : base(context, runner, output, config, history)
    {
    }

    public override string Kind => "nav";

    public override string RunButtonText => "BUILD NAVIGATION";

    protected override string ActionName => "Navigation build";

    protected override IReadOnlyList<string> ArgumentsFor(string map)
        => NavigationBuilder.BuildArguments(Context.ProjectFile, map);

    protected override IReadOnlyList<string> ValidateInputs(IReadOnlyList<string> maps)
        => NavigationBuilder.Validate(Context.ProjectFile, maps);
}
