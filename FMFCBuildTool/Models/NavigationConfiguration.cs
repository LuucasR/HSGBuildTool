using System.Collections.Generic;

namespace FMFCBuildTool.Models;

public class NavigationConfiguration
{
    public string UnrealEditorCmd { get; set; } = "";

    public string ProjectFile { get; set; } = "";

    public List<string> Maps { get; set; } = new();
}