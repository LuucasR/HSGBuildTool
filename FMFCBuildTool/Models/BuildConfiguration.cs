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

    public BuildConfiguration Clone()
    {
        var clone = new BuildConfiguration
        {
            ProjectFile = ProjectFile,
            ProjectDirectory = ProjectDirectory,
            ArchiveDirectory = ArchiveDirectory,
            RunUAT = RunUAT,
            Configuration = Configuration,
            UnrealEditorCmd = UnrealEditorCmd,

            FileOpenLog = FileOpenLog,
            StdOut = StdOut,
            CrashForUAT = CrashForUAT,
            Unattended = Unattended,
            NoLogTimes = NoLogTimes,

            Prereqs = Prereqs,
            Distribution = Distribution,
            CrashReporter = CrashReporter,
            Client = Client,
            Server = Server,

            Build = Build,
            Cook = Cook,
            Stage = Stage,
            Package = Package,
            Archive = Archive,
            Pak = Pak,
            IoStore = IoStore,

            Compressed = Compressed,
            FullCook = FullCook,
            UseProjectDefaultMaps = UseProjectDefaultMaps,

            NoCompile = NoCompile,
            NoCompileEditor = NoCompileEditor,
            UnversionedCookedContent = UnversionedCookedContent,
            CookIncremental = CookIncremental,
            ZenStore = ZenStore,

            SkipCookingEditorContent = SkipCookingEditorContent,

            CookCultures = new List<string>(CookCultures)
        };

        clone.Maps.AddRange(Maps);

        return clone;
    }
}