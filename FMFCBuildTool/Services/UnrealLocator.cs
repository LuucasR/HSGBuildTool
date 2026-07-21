using System.Text.Json;
using System.IO;
namespace FMFCBuildTool.Services;

public static class UnrealLocator
{
    public static string FindRunUAT(string projectFile)
    {
        if (!File.Exists(projectFile))
            throw new FileNotFoundException(projectFile);

        using var doc = JsonDocument.Parse(File.ReadAllText(projectFile));

        if (!doc.RootElement.TryGetProperty("EngineAssociation", out var association))
            throw new Exception("EngineAssociation not found in .uproject");

        var version = association.GetString();

        if (string.IsNullOrWhiteSpace(version))
            throw new Exception("Invalid EngineAssociation.");

        var possibleRoots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Epic Games",
                $"UE_{version}"),

            Path.Combine(
                "C:\\Program Files",
                "Epic Games",
                $"UE_{version}"),

            Path.Combine(
                "D:\\Epic Games",
                $"UE_{version}")
        };


        foreach(var root in possibleRoots)
        {
            var runUAT = Path.Combine(
                root,
                "Engine",
                "Build",
                "BatchFiles",
                "RunUAT.bat");

            if(File.Exists(runUAT))
                return runUAT;
        }


        throw new FileNotFoundException(
            $"Could not find Unreal Engine {version} RunUAT.");
    }


    public static string FindEngineRoot(string projectFile)
    {
        var runUAT = FindRunUAT(projectFile);

        return Directory.GetParent(
                Directory.GetParent(
                    Directory.GetParent(runUAT)!.FullName)!.FullName)!
            .FullName;
    }
}