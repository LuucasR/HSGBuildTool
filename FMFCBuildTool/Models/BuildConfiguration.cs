namespace FMFCBuildTool.Models;

public class BuildConfiguration
{
    public string ProjectFile { get; set; } = "";

    public string ProjectDirectory { get; set; } = "";

    public string ArchiveDirectory { get; set; } = "";

    public string EngineRoot { get; set; } = "";

    public string RunUAT { get; set; } = "";

    public string Configuration { get; set; } = "Shipping";

    public bool Build { get; set; } = true;

    public bool Cook { get; set; } = true;

    public bool Stage { get; set; } = true;

    public bool Package { get; set; } = true;

    public bool Archive { get; set; } = true;

    public bool Pak { get; set; } = true;

    public bool Compressed { get; set; } = true;

    public bool FullCook { get; set; }

    public bool UseProjectDefaultMaps { get; set; } = true;

    public List<string> Maps { get; } = new();
}