namespace FMFCBuildTool.Models;

public class BuildConfiguration
{
    public string ProjectFile { get; set; } = "";
    public string ProjectDirectory { get; set; } = "";
    public string ArchiveDirectory { get; set; } = "";
    public string RunUAT { get; set; } = "";
    public string Configuration { get; set; } = "";

    public string UnrealEditorCmd { get; set; } = "";
    
    public bool FileOpenLog { get; set; } = true;
    public bool StdOut { get; set; } = true;
    public bool CrashForUAT { get; set; } = true;
    public bool Unattended { get; set; } = true;
    public bool NoLogTimes { get; set; } = true;
    
    public bool Prereqs { get; set; }
    public bool Distribution { get; set; }
    public bool CrashReporter { get; set; }
    public bool Client { get; set; }
    public bool Server { get; set; }
    
    public bool Build { get; set; }
    public bool Cook { get; set; }
    public bool Stage { get; set; }
    public bool Package { get; set; }
    public List<string> CookCultures { get; set; } = new() { "en" };
    public bool Archive { get; set; }
    public bool Pak { get; set; }
    public bool IoStore { get; set; } = true;
    
    public bool Compressed { get; set; }
    public bool FullCook { get; set; }
    public bool UseProjectDefaultMaps { get; set; }

    public bool NoCompile { get; set; } = true;
    public bool NoCompileEditor { get; set; } = true;
    public bool UnversionedCookedContent { get; set; } = true;
    public bool CookIncremental { get; set; }
    public bool ZenStore { get; set; }

    public bool SkipCookingEditorContent { get; set; }
    public List<string> Maps { get; } = new();
}