using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>
/// Persists <see cref="AppConfig"/> to %APPDATA%\FMFCBuildTool\config.json.
/// </summary>
/// <remarks>
/// Was static and wrote next to the .exe, which meant two views each held their own
/// copy of the config and clobbered each other's fields, and saving failed silently
/// if the tool ever lived somewhere unwritable. Now instance-based (one per app),
/// writes atomically, and reports failures through <see cref="Error"/> instead of
/// throwing into a MessageBox.
/// </remarks>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    public ConfigService()
    {
        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FMFCBuildTool");

        ConfigPath = Path.Combine(ConfigDirectory, "config.json");

        LogDirectory = Path.Combine(ConfigDirectory, "Logs");
    }

    public string ConfigDirectory { get; }

    public string ConfigPath { get; }

    public string LogDirectory { get; }

    /// <summary>Raised on a load or save problem. Wired to the output log by App.</summary>
    public event Action<string>? Error;

    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return ImportLegacyConfig() ?? new AppConfig();

            var json = File.ReadAllText(ConfigPath);

            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            // Don't reset the user's setup behind their back: keep the unreadable file
            // around so it can be inspected, and say so.
            TryBackupCorruptConfig();

            Error?.Invoke($"Could not read {ConfigPath} ({ex.Message}). Starting from defaults; the previous file was kept as config.corrupt.json.");

            return new AppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);

            var json = JsonSerializer.Serialize(config, SerializerOptions);

            // Write to a temp file and swap, so a crash mid-write cannot leave a
            // truncated config behind.
            var temp = ConfigPath + ".tmp";

            File.WriteAllText(temp, json);
            File.Move(temp, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Could not save settings to {ConfigPath}: {ex.Message}");
        }
    }

    private void TryBackupCorruptConfig()
    {
        try
        {
            var backup = Path.Combine(ConfigDirectory, "config.corrupt.json");

            File.Copy(ConfigPath, backup, overwrite: true);
        }
        catch
        {
            // Best effort only — the caller already reports the underlying failure.
        }
    }

    /// <summary>
    /// One-time migration of the v1 config that lived beside the executable, so the
    /// per-project setups built up under the old tool survive the move to %APPDATA%.
    /// </summary>
    private AppConfig? ImportLegacyConfig()
    {
        try
        {
            var legacyPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Config",
                "Config.json");

            if (!File.Exists(legacyPath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(legacyPath));

            var root = doc.RootElement;
            var config = new AppConfig();

            if (root.TryGetProperty("LastProject", out var lastProject))
                config.LastProject = lastProject.GetString() ?? "";

            if (root.TryGetProperty("LastEnginePath", out var enginePath))
                config.EnginePathOverride = enginePath.GetString() ?? "";

            if (root.TryGetProperty("LastArchiveFolder", out var archive))
                config.DefaultArchiveRoot = archive.GetString() ?? "";

            if (root.TryGetProperty("ProjectConfigurations", out var projects) &&
                projects.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in projects.EnumerateObject())
                {
                    var preset = ReadLegacyPreset(entry.Value);

                    config.Projects[entry.Name] = new ProjectSettings
                    {
                        ActivePreset = preset.Name,
                        Presets = { preset }
                    };

                    config.TouchRecent(entry.Name);
                }
            }

            if (!string.IsNullOrWhiteSpace(config.LastProject))
                config.TouchRecent(config.LastProject);

            Save(config);

            return config;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Could not import the previous configuration: {ex.Message}. Starting from defaults.");

            return null;
        }
    }

    private static BuildPreset ReadLegacyPreset(JsonElement element)
    {
        bool Flag(string name, bool fallback = false)
            => element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? v.GetBoolean()
                : fallback;

        string Text(string name, string fallback = "")
            => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? fallback
                : fallback;

        List<string> Strings(string name)
        {
            var list = new List<string>();

            if (element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in v.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                        list.Add(s);
                }
            }

            return list;
        }

        var cultures = Strings("CookCultures");

        return new BuildPreset
        {
            Name = BuildPreset.DefaultName,

            Configuration = Text("Configuration", "Shipping"),
            Client = Flag("Client"),
            Server = Flag("Server"),

            Build = Flag("Build", true),
            Cook = Flag("Cook", true),
            Stage = Flag("Stage", true),
            Package = Flag("Package", true),
            Archive = Flag("Archive", true),
            ArchiveDirectory = Text("ArchiveDirectory"),

            FullCook = Flag("FullCook"),
            CookIncremental = Flag("CookIncremental"),
            ZenStore = Flag("ZenStore"),
            SkipCookingEditorContent = Flag("SkipCookingEditorContent", true),
            UnversionedCookedContent = Flag("UnversionedCookedContent", true),
            CookCultures = cultures.Count > 0 ? cultures : new List<string> { "en" },

            Pak = Flag("Pak", true),
            IoStore = Flag("IoStore", true),
            Compressed = Flag("Compressed", true),
            Prereqs = Flag("Prereqs"),
            Distribution = Flag("Distribution"),
            CrashReporter = Flag("CrashReporter"),

            NoCompile = Flag("NoCompile", true),
            NoCompileEditor = Flag("NoCompileEditor", true),
            FileOpenLog = Flag("FileOpenLog", true),
            StdOut = Flag("StdOut", true),
            CrashForUAT = Flag("CrashForUAT", true),
            Unattended = Flag("Unattended", true),
            NoLogTimes = Flag("NoLogTimes", true),

            UseProjectDefaultMaps = Flag("UseProjectDefaultMaps"),
            Maps = Strings("Maps")
        };
    }
}
