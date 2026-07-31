using System.Collections.Generic;

namespace FMFCBuildTool.Models;

public class AppConfig
{
    public string LastProject { get; set; } = "";

    /// <summary>Page to restore on startup. The old LastAction was written but never read back.</summary>
    public string LastPage { get; set; } = "Package";

    /// <summary>
    /// Manual engine root, used only when auto-detection from the .uproject fails.
    /// The old LastEnginePath was the *only* source for the Navigation page, which is
    /// why Package and Navigation could disagree about which engine they were using.
    /// </summary>
    public string EnginePathOverride { get; set; } = "";

    public string DefaultArchiveRoot { get; set; } = "";

    public int LogRetentionDays { get; set; } = 14;

    public double LogDockHeight { get; set; } = 220;

    public List<string> RecentProjects { get; set; } = new();

    public Dictionary<string, ProjectSettings> Projects { get; set; } = new();

    public ProjectSettings GetOrCreate(string projectFile)
    {
        if (!Projects.TryGetValue(projectFile, out var settings))
        {
            settings = new ProjectSettings();
            settings.Presets.Add(new BuildPreset());

            Projects[projectFile] = settings;
        }

        if (settings.Presets.Count == 0)
            settings.Presets.Add(new BuildPreset());

        return settings;
    }

    public void TouchRecent(string projectFile)
    {
        if (string.IsNullOrWhiteSpace(projectFile))
            return;

        RecentProjects.RemoveAll(p => string.Equals(p, projectFile, System.StringComparison.OrdinalIgnoreCase));
        RecentProjects.Insert(0, projectFile);

        if (RecentProjects.Count > 10)
            RecentProjects.RemoveRange(10, RecentProjects.Count - 10);
    }
}
