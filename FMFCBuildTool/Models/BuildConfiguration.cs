namespace FMFCBuildTool.Models;

public class BuildConfiguration
{
    public string ProjectFile { get; set; } = "";
    public string ProjectDirectory { get; set; } = "";
    public string ArchiveDirectory { get; set; } = "";
    public string RunUAT { get; set; } = "";
    public string Configuration { get; set; } = "";

    public bool Build { get; set; }
    public bool Cook { get; set; }
    public bool Stage { get; set; }
    public bool Package { get; set; }
    public bool Archive { get; set; }
    public bool Pak { get; set; }
    public bool Compressed { get; set; }
    public bool FullCook { get; set; }
    public bool UseProjectDefaultMaps { get; set; }

    public bool NoCompile { get; set; } = true;
    public bool NoCompileEditor { get; set; } = true;
    public bool UnversionedCookedContent { get; set; } = true;
    public bool CookIncremental { get; set; }
    public bool ZenStore { get; set; }

    public List<string> Maps { get; } = new();
}