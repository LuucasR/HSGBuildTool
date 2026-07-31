using System.Text.Json;
using FMFCBuildTool.Models;
using Xunit;

namespace FMFCBuildTool.Tests;

public class BuildPresetTests
{
    /// <summary>
    /// Maps used to be a get-only property. System.Text.Json serialised it happily but
    /// refused to repopulate it, so every saved map selection was silently lost on load.
    /// </summary>
    [Fact]
    public void Map_selection_survives_a_serialisation_round_trip()
    {
        var preset = new BuildPreset
        {
            Name = "Shipping-QA",
            Maps = { "/Game/Maps/L_Arena", "/Game/Maps/L_Hub" },
            CookCultures = { "es" }
        };

        var restored = JsonSerializer.Deserialize<BuildPreset>(JsonSerializer.Serialize(preset))!;

        Assert.Equal(new[] { "/Game/Maps/L_Arena", "/Game/Maps/L_Hub" }, restored.Maps);
        Assert.Contains("es", restored.CookCultures);
        Assert.Equal("Shipping-QA", restored.Name);
    }

    [Fact]
    public void Clone_is_deep()
    {
        var preset = new BuildPreset { Maps = { "/Game/Maps/A" } };

        var clone = preset.Clone();
        clone.Maps.Add("/Game/Maps/B");
        clone.Pak = !preset.Pak;

        Assert.Single(preset.Maps);
        Assert.NotEqual(preset.Pak, clone.Pak);
    }

    /// <summary>A cloned preset must carry every option, including ones added later.</summary>
    [Fact]
    public void Clone_copies_every_option()
    {
        var preset = new BuildPreset
        {
            Platform = "Linux",
            Configuration = "Development",
            Client = true,
            Server = true,
            ZenStore = true,
            Distribution = true,
            ArchiveDirectory = @"D:\Builds",
            UseProjectDefaultMaps = false
        };

        var clone = preset.Clone();

        Assert.Equal(JsonSerializer.Serialize(preset), JsonSerializer.Serialize(clone));
    }

    [Fact]
    public void Project_settings_fall_back_to_the_first_preset()
    {
        var settings = new ProjectSettings { ActivePreset = "missing" };
        settings.Presets.Add(new BuildPreset { Name = "Default" });

        Assert.Equal("Default", settings.GetActivePreset().Name);
    }

    [Fact]
    public void Recent_projects_are_deduplicated_and_most_recent_first()
    {
        var config = new AppConfig();

        config.TouchRecent(@"D:\A\A.uproject");
        config.TouchRecent(@"D:\B\B.uproject");
        config.TouchRecent(@"D:\A\A.uproject");

        Assert.Equal(2, config.RecentProjects.Count);
        Assert.Equal(@"D:\A\A.uproject", config.RecentProjects[0]);
    }
}
