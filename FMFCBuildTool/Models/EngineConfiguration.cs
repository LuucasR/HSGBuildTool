namespace FMFCBuildTool.Models;
using System.IO;

public class EngineConfiguration
{
    public string EnginePath { get; set; } = "";

    public string EditorCmd =>
        Path.Combine(
            EnginePath,
            "Engine",
            "Binaries",
            "Win64",
            "UnrealEditor-Cmd.exe");
}