using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using FMFCBuildTool.Models;
using Microsoft.Win32;

namespace FMFCBuildTool.Services;

/// <summary>
/// Resolves the Unreal Engine installation a project should build with.
/// </summary>
/// <remarks>
/// The previous version probed three hardcoded paths (Program Files\Epic Games\UE_x
/// and D:\Epic Games\UE_x), so an engine installed anywhere else — or built from
/// source, where EngineAssociation is a GUID — simply could not be found. It also
/// threw for every failure, which surfaced as a stack trace in a MessageBox.
/// </remarks>
public static class UnrealLocator
{
    private const string RunUATRelative = @"Engine\Build\BatchFiles\RunUAT.bat";
    private const string EditorCmdRelative = @"Engine\Binaries\Win64\UnrealEditor-Cmd.exe";

    /// <summary>
    /// Resolves the engine for <paramref name="projectFile"/>, preferring an explicit
    /// <paramref name="overrideRoot"/> when the user has set one in Settings.
    /// Never throws — failures come back through <paramref name="error"/>.
    /// </summary>
    public static bool TryResolve(
        string projectFile,
        string overrideRoot,
        [NotNullWhen(true)] out EnginePaths? engine,
        out string error)
    {
        engine = null;
        error = "";

        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            engine = FromRoot(overrideRoot, ReadVersionFromRoot(overrideRoot), "Manual override");

            if (engine is not null)
                return true;

            error = $"The engine path set in Settings does not contain {RunUATRelative}:\n{overrideRoot}";
            return false;
        }

        if (!ProjectLoader.IsValidProject(projectFile))
        {
            error = "No project selected.";
            return false;
        }

        string association;

        try
        {
            association = ReadEngineAssociation(projectFile);
        }
        catch (Exception ex)
        {
            error = $"Could not read EngineAssociation from the .uproject: {ex.Message}";
            return false;
        }

        // Source builds registered by Setup.bat use a GUID instead of a version.
        if (association.StartsWith('{'))
        {
            var root = ReadSourceBuildRoot(association);

            if (root is not null)
            {
                engine = FromRoot(root, "Source", "Registered source build");

                if (engine is not null)
                    return true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(association))
        {
            engine = FindInstalled(association);

            if (engine is not null)
                return true;
        }

        // Foreign/in-tree project: walk up looking for a sibling Engine folder.
        engine = FindInParentDirectories(projectFile);

        if (engine is not null)
            return true;

        error = string.IsNullOrWhiteSpace(association)
            ? "The .uproject has no EngineAssociation. Set the engine path manually in Settings."
            : $"Could not find Unreal Engine \"{association}\". Set the engine path manually in Settings.";

        return false;
    }

    public static string ReadEngineAssociation(string projectFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(projectFile));

        return doc.RootElement.TryGetProperty("EngineAssociation", out var association)
            ? association.GetString() ?? ""
            : "";
    }

    /// <summary>Builds an <see cref="EnginePaths"/> from an engine root, or null if it isn't one.</summary>
    public static EnginePaths? FromRoot(string root, string version, string source)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        var runUAT = Path.Combine(root, RunUATRelative);
        var editorCmd = Path.Combine(root, EditorCmdRelative);

        // Both are required: the old code validated RunUAT for packaging but not
        // UnrealEditor-Cmd for the nav/lighting commandlets, so a broken editor path
        // only failed once a build was already underway.
        if (!File.Exists(runUAT) || !File.Exists(editorCmd))
            return null;

        return new EnginePaths
        {
            Root = root,
            RunUAT = runUAT,
            EditorCmd = editorCmd,
            Version = version,
            Source = source,
            IsInstalled = File.Exists(Path.Combine(root, "Engine", "Build", "InstalledBuild.txt"))
        };
    }

    /// <summary>Every engine this machine knows about. Shown in Settings.</summary>
    public static IReadOnlyList<EnginePaths> DiscoverInstalled()
    {
        var found = new List<EnginePaths>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(EnginePaths? engine)
        {
            if (engine is not null && seen.Add(engine.Root))
                found.Add(engine);
        }

        foreach (var (version, root) in ReadRegistryInstallations())
            Add(FromRoot(root, version, "Registry"));

        foreach (var (version, root) in ReadLauncherInstallations())
            Add(FromRoot(root, version, "Epic Games Launcher"));

        foreach (var (guid, root) in ReadSourceBuilds())
            Add(FromRoot(root, "Source", $"Source build {guid}"));

        foreach (var root in EnumerateConventionalRoots())
            Add(FromRoot(root, ReadVersionFromRoot(root), "Conventional path"));

        return found
            .OrderByDescending(e => e.Version)
            .ToList();
    }

    private static EnginePaths? FindInstalled(string version)
    {
        foreach (var (candidateVersion, root) in ReadRegistryInstallations())
        {
            if (candidateVersion == version && FromRoot(root, version, "Registry") is { } engine)
                return engine;
        }

        foreach (var (candidateVersion, root) in ReadLauncherInstallations())
        {
            if (candidateVersion == version && FromRoot(root, version, "Epic Games Launcher") is { } engine)
                return engine;
        }

        foreach (var root in EnumerateConventionalRoots())
        {
            if (Path.GetFileName(root).Equals($"UE_{version}", StringComparison.OrdinalIgnoreCase) &&
                FromRoot(root, version, "Conventional path") is { } engine)
            {
                return engine;
            }
        }

        return null;
    }

    /// <summary>HKLM\SOFTWARE\EpicGames\Unreal Engine\&lt;version&gt; — written by the installer.</summary>
    private static IEnumerable<(string Version, string Root)> ReadRegistryInstallations()
    {
        var results = new List<(string, string)>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine");

            if (key is null)
                return results;

            foreach (var version in key.GetSubKeyNames())
            {
                using var versionKey = key.OpenSubKey(version);

                if (versionKey?.GetValue("InstalledDirectory") is string dir && !string.IsNullOrWhiteSpace(dir))
                    results.Add((version, dir));
            }
        }
        catch
        {
            // Registry unreadable (permissions, redirected hive) — fall through to the other probes.
        }

        return results;
    }

    /// <summary>HKCU\Software\Epic Games\Unreal Engine\Builds — GUID-keyed source builds.</summary>
    private static IEnumerable<(string Guid, string Root)> ReadSourceBuilds()
    {
        var results = new List<(string, string)>();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");

            if (key is null)
                return results;

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is string dir && !string.IsNullOrWhiteSpace(dir))
                    results.Add((name, dir));
            }
        }
        catch
        {
        }

        return results;
    }

    private static string? ReadSourceBuildRoot(string guid)
    {
        return ReadSourceBuilds()
            .FirstOrDefault(b => string.Equals(b.Guid, guid, StringComparison.OrdinalIgnoreCase))
            .Root;
    }

    /// <summary>C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat — Launcher installs.</summary>
    private static IEnumerable<(string Version, string Root)> ReadLauncherInstallations()
    {
        var results = new List<(string, string)>();

        try
        {
            var manifest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");

            if (!File.Exists(manifest))
                return results;

            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));

            if (!doc.RootElement.TryGetProperty("InstallationList", out var list) ||
                list.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in list.EnumerateArray())
            {
                if (!item.TryGetProperty("InstallLocation", out var location) ||
                    location.GetString() is not { } root ||
                    string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var appName = item.TryGetProperty("AppName", out var name) ? name.GetString() ?? "" : "";

                // AppName looks like "UE_5.4"; anything else in this manifest is a plugin or sample.
                if (!appName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add((appName[3..], root));
            }
        }
        catch
        {
        }

        return results;
    }

    /// <summary>
    /// "Epic Games\UE_x" under every fixed drive, plus Program Files. Replaces the
    /// three hardcoded paths — an engine on E: is now found without manual setup.
    /// </summary>
    private static IEnumerable<string> EnumerateConventionalRoots()
    {
        var parents = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games")
        };

        try
        {
            parents.AddRange(DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => Path.Combine(d.RootDirectory.FullName, "Epic Games")));
        }
        catch
        {
        }

        foreach (var parent in parents.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] children;

            try
            {
                if (!Directory.Exists(parent))
                    continue;

                children = Directory.GetDirectories(parent, "UE_*");
            }
            catch
            {
                continue;
            }

            foreach (var child in children)
                yield return child;
        }
    }

    /// <summary>Handles projects living inside a source tree (…\UnrealEngine\MyProject\X.uproject).</summary>
    private static EnginePaths? FindInParentDirectories(string projectFile)
    {
        var directory = Directory.GetParent(projectFile);

        while (directory is not null)
        {
            if (FromRoot(directory.FullName, ReadVersionFromRoot(directory.FullName), "Found next to the project") is { } engine)
                return engine;

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>Reads the real version from Engine\Build\Build.version, falling back to the folder name.</summary>
    private static string ReadVersionFromRoot(string root)
    {
        try
        {
            var versionFile = Path.Combine(root, "Engine", "Build", "Build.version");

            if (File.Exists(versionFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(versionFile));

                if (doc.RootElement.TryGetProperty("MajorVersion", out var major) &&
                    doc.RootElement.TryGetProperty("MinorVersion", out var minor))
                {
                    return $"{major.GetInt32()}.{minor.GetInt32()}";
                }
            }
        }
        catch
        {
        }

        var folder = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));

        return folder.StartsWith("UE_", StringComparison.OrdinalIgnoreCase) ? folder[3..] : "";
    }
}
