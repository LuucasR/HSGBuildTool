using FMFCBuildTool.Core;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.Models;

/// <summary>
/// Session state shared by every page: which project is open and which engine
/// resolved for it. Observable so the title bar, the rail and all three build
/// pages react to a project switch without being recreated.
/// </summary>
public sealed class BuildContext : ObservableObject
{
    private string _projectFile = "";
    private EnginePaths? _engine;
    private string _engineError = "";

    public string ProjectFile
    {
        get => _projectFile;
        set
        {
            if (!SetProperty(ref _projectFile, value))
                return;

            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(ProjectDirectory));
            OnPropertyChanged(nameof(HasProject));
        }
    }

    public EnginePaths? Engine
    {
        get => _engine;
        set
        {
            if (!SetProperty(ref _engine, value))
                return;

            OnPropertyChanged(nameof(HasEngine));
            OnPropertyChanged(nameof(EngineLabel));
        }
    }

    /// <summary>Why engine resolution failed, shown in the title-bar badge tooltip.</summary>
    public string EngineError
    {
        get => _engineError;
        set => SetProperty(ref _engineError, value);
    }

    public bool HasProject => ProjectLoader.IsValidProject(ProjectFile);

    public string ProjectName => HasProject ? ProjectLoader.GetProjectName(ProjectFile) : "";

    public string ProjectDirectory => HasProject ? ProjectLoader.GetProjectDirectory(ProjectFile) : "";

    public bool HasEngine => Engine is not null;

    public string EngineLabel => Engine is null ? "No engine" : Engine.ToString();
}
