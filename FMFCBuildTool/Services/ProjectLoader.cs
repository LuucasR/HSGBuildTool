using System.IO;

namespace FMFCBuildTool.Services;

public static class ProjectLoader
{
    public static bool IsValidProject(string projectFile)
    {
        return File.Exists(projectFile) &&
               Path.GetExtension(projectFile).Equals(".uproject", StringComparison.OrdinalIgnoreCase);
    }

    public static string GetProjectName(string projectFile)
    {
        return Path.GetFileNameWithoutExtension(projectFile);
    }

    public static string GetProjectDirectory(string projectFile)
    {
        return Path.GetDirectoryName(projectFile)!;
    }

    public static string GetContentDirectory(string projectFile)
    {
        return Path.Combine(GetProjectDirectory(projectFile), "Content");
    }

    public static string GetConfigDirectory(string projectFile)
    {
        return Path.Combine(GetProjectDirectory(projectFile), "Config");
    }
}