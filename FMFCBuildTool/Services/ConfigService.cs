using System;
using System.IO;
using System.Text.Json;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

public static class ConfigService
{
    private static readonly string ConfigPath =
        Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Config",
            "Config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return new AppConfig();

            var json = File.ReadAllText(ConfigPath);

            return JsonSerializer.Deserialize<AppConfig>(json)
                   ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(ConfigPath)!);

        var json = JsonSerializer.Serialize(
            config,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });

        File.WriteAllText(ConfigPath, json);
    }
}