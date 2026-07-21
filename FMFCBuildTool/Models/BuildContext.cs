namespace FMFCBuildTool.Models;

public class BuildContext
{
    public string ProjectFile { get; set; } = "";

    public string ProjectDirectory { get; set; } = "";

    public string EnginePath { get; set; } = "";

    public List<MapItem> Maps { get; set; } = new();
}