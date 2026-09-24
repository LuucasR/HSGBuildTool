using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>How the Launch page runs the game.</summary>
public enum LaunchMode
{
    /// <summary>One -game process, offline.</summary>
    Standalone,

    /// <summary>A -game process hosting with ?listen, plus N clients connecting to it.</summary>
    ListenServer,

    /// <summary>A -server process with no window, plus N clients connecting to it.</summary>
    DedicatedServer,

    /// <summary>N -game clients connecting to an address, for a server running elsewhere.</summary>
    ClientOnly,

    /// <summary>A -server process alone, for clients started elsewhere.</summary>
    DedicatedServerOnly
}

/// <summary>A mode as the page's ComboBox shows it.</summary>
public sealed record LaunchModeOption(LaunchMode Id, string Display, string Description);

/// <summary>Everything the Launch page lets you choose, in one value.</summary>
public sealed record LaunchOptions(
    LaunchMode Mode,
    string Map,
    int ClientCount,
    int Port,
    string ConnectAddress,
    bool Windowed,
    int ResX,
    int ResY,
    bool TileWindows,
    string GameMode,
    string UrlOptions,
    string Rhi,
    bool NoSound,
    bool ShowLogConsole,
    string ExecCmds,
    bool NoSteam,
    string ExtraArguments,
    string Scalability = "Default");

/// <summary>One process the launch starts.</summary>
/// <param name="Label">"Game", "Server", "Host" or "Client 2" — prefixes its lines in Output.</param>
/// <param name="IsServer">Started first, and given a head start before the clients.</param>
/// <param name="LogFile">Where -abslog sends its log, which the tool tails into Output.</param>
public sealed record LaunchInstance(string Label, bool IsServer, IReadOnlyList<string> Arguments, string LogFile);

/// <summary>The screen area windows are tiled into. A plain record so tests need no WPF.</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height);

/// <summary>
/// Command lines for running the project out of the editor binaries — UnrealEditor.exe with
/// -game or -server — which is as close to the packaged game as it gets without cooking.
/// </summary>
/// <remarks>
/// Pure: the page, the .bat export and the tests all read the same argument lists. Every
/// process gets its own -abslog, because several instances writing the default
/// Saved\Logs\&lt;Project&gt;.log at once would overwrite each other, and because a GUI-subsystem
/// exe's stdout is not something to rely on — the log file is.
/// </remarks>
public static class LaunchBuilder
{
    public const int MaxClients = 8;

    public static IReadOnlyList<LaunchModeOption> Modes { get; } = new[]
    {
        new LaunchModeOption(LaunchMode.Standalone, "Standalone (offline)",
            "One game process with no networking, like the packaged game started on its own."),
        new LaunchModeOption(LaunchMode.ListenServer, "Listen server + clients",
            "The first window hosts with ?listen and plays; the clients connect to it."),
        new LaunchModeOption(LaunchMode.DedicatedServer, "Dedicated server + clients",
            "A -server process with no window, and game clients connecting to it."),
        new LaunchModeOption(LaunchMode.ClientOnly, "Client only",
            "Game clients connecting to a server at the address below, started elsewhere."),
        new LaunchModeOption(LaunchMode.DedicatedServerOnly, "Dedicated server only",
            "A -server process alone, for clients started from another machine or a packaged build.")
    };

    public static IReadOnlyList<string> Rhis { get; } = new[] { "Default", "DX12", "DX11", "Vulkan" };

    /// <summary>"Default" leaves the project's and the player's saved settings alone.</summary>
    public static IReadOnlyList<string> ScalabilityLevels { get; } = new[] { "Default", "Low", "Medium", "High", "Epic", "Cinematic" };

    /// <summary>
    /// The scalability groups a level applies to. Not sg.ResolutionQuality: that one is a
    /// screen percentage rather than a level, and the Scalability console command's 50% at
    /// Low would make a quick look at a map blurry for no gain in load time.
    /// </summary>
    private static readonly string[] ScalabilityGroups =
    {
        "sg.ViewDistanceQuality", "sg.AntiAliasingQuality", "sg.ShadowQuality", "sg.GlobalIlluminationQuality",
        "sg.ReflectionQuality", "sg.PostProcessQuality", "sg.TextureQuality", "sg.EffectsQuality",
        "sg.FoliageQuality", "sg.ShadingQuality", "sg.LandscapeQuality"
    };

    public static bool HasServer(LaunchMode mode)
        => mode is LaunchMode.ListenServer or LaunchMode.DedicatedServer or LaunchMode.DedicatedServerOnly;

    public static bool HasClients(LaunchMode mode)
        => mode is LaunchMode.ListenServer or LaunchMode.DedicatedServer or LaunchMode.ClientOnly;

    /// <summary>The processes to start, server first.</summary>
    public static IReadOnlyList<LaunchInstance> Build(string projectFile, LaunchOptions options, ScreenArea screen)
    {
        var instances = new List<LaunchInstance>();
        var logFolder = LogFolder(projectFile);
        var clients = Math.Clamp(options.ClientCount, 1, MaxClients);

        // Only the processes with a window are tiled, so the dedicated server does not take a
        // slot on screen that nothing will ever occupy.
        var windowCount = options.Mode switch
        {
            LaunchMode.Standalone => 1,
            LaunchMode.ListenServer => clients + 1,
            LaunchMode.DedicatedServer or LaunchMode.ClientOnly => clients,
            _ => 0
        };

        var tiles = Tiles(windowCount, options, screen);
        var window = 0;

        switch (options.Mode)
        {
            case LaunchMode.Standalone:
                instances.Add(Game("Game", projectFile, MapUrl(options, listen: false), options, tiles, window, logFolder));
                break;

            case LaunchMode.ListenServer:
                instances.Add(Game("Host", projectFile, MapUrl(options, listen: true), options, tiles, window++, logFolder, isServer: true));
                break;

            case LaunchMode.DedicatedServer:
            case LaunchMode.DedicatedServerOnly:
                instances.Add(Server(projectFile, options, logFolder));
                break;
        }

        if (HasClients(options.Mode))
        {
            var address = options.Mode == LaunchMode.ClientOnly ? options.ConnectAddress.Trim() : "127.0.0.1";
            var url = $"{address}:{options.Port}{options.UrlOptions.Trim()}";

            for (var i = 1; i <= clients; i++)
                instances.Add(Game($"Client {i}", projectFile, url, options, tiles, window++, logFolder));
        }

        return instances;
    }

    /// <summary>What is wrong with launching as things stand. Empty when LAUNCH can go.</summary>
    public static IReadOnlyList<string> Validate(string projectFile, EnginePaths? engine, LaunchOptions options)
    {
        var problems = new List<string>();

        if (!ProjectLoader.IsValidProject(projectFile))
        {
            problems.Add("Open a project first.");
            return problems;
        }

        if (engine is not null && !File.Exists(engine.Editor))
            problems.Add($"UnrealEditor.exe is not in this engine: {engine.Editor}");

        if (MissingEditorModule(projectFile) is { } module)
            problems.Add($"The project's editor binaries are not built ({module} is missing). Compile the editor target on the Compiler page first.");

        if (options.Mode == LaunchMode.ListenServer && string.IsNullOrWhiteSpace(options.Map))
            problems.Add("A listen server needs a map to host: ?listen goes on the map's URL.");

        if (!HasServer(options.Mode) && options.Mode != LaunchMode.Standalone)
        {
            if (string.IsNullOrWhiteSpace(options.ConnectAddress))
                problems.Add("Enter the address of the server to connect to.");
        }

        if (options.Mode is LaunchMode.Standalone or LaunchMode.DedicatedServer or LaunchMode.DedicatedServerOnly
            && string.IsNullOrWhiteSpace(options.Map)
            && (options.GameMode.Trim().Length > 0 || options.UrlOptions.Trim().Length > 0))
        {
            problems.Add("A GameMode override or URL options need a map to go on: pick one instead of the project default.");
        }

        if (options.UrlOptions.Trim() is { Length: > 0 } url && !url.StartsWith('?'))
            problems.Add("URL options start with ?, e.g. ?Name=Tester.");

        if ((HasServer(options.Mode) || HasClients(options.Mode)) && options.Port is < 1 or > 65535)
            problems.Add("The port must be between 1 and 65535.");

        if (HasClients(options.Mode) && options.ClientCount is < 1 or > MaxClients)
            problems.Add($"Launch between 1 and {MaxClients} clients.");

        if (options.Windowed && (options.ResX < 320 || options.ResY < 240))
            problems.Add("The window resolution is too small to play in (minimum 320×240).");

        return problems;
    }

    /// <summary>Where each instance's -abslog goes: Saved\Logs\Launch under the project.</summary>
    public static string LogFolder(string projectFile)
        => Path.Combine(ProjectLoader.GetProjectDirectory(projectFile), "Saved", "Logs", "Launch");

    /// <summary>Quotes what needs it, the way cmd.exe and Process.Start both read it.</summary>
    public static string ToCommandLine(IEnumerable<string> arguments) => string.Join(" ", arguments);

    // ---------------------------------------------------------------- pieces

    private static LaunchInstance Game(
        string label,
        string projectFile,
        string url,
        LaunchOptions options,
        IReadOnlyList<(int X, int Y, int W, int H)> tiles,
        int window,
        string logFolder,
        bool isServer = false)
    {
        var args = new List<string> { Quote(projectFile) };

        if (url.Length > 0)
            args.Add(url);

        args.Add("-game");

        if (isServer)
            args.Add($"-port={options.Port}");

        if (options.Windowed)
        {
            args.Add("-windowed");

            if (window < tiles.Count)
            {
                var (x, y, w, h) = tiles[window];

                args.Add($"-ResX={w}");
                args.Add($"-ResY={h}");
                args.Add($"-WinX={x}");
                args.Add($"-WinY={y}");
            }
            else
            {
                args.Add($"-ResX={options.ResX}");
                args.Add($"-ResY={options.ResY}");
            }
        }
        else
        {
            args.Add("-fullscreen");
        }

        switch (options.Rhi)
        {
            case "DX12": args.Add("-dx12"); break;
            case "DX11": args.Add("-dx11"); break;
            case "Vulkan": args.Add("-vulkan"); break;
        }

        if (options.NoSound)
            args.Add("-nosound");

        if (ScalabilityArgument(options.Scalability) is { } scalability)
            args.Add(scalability);

        AddCommon(args, options, label, logFolder);

        return new LaunchInstance(label, isServer, args, LogFile(logFolder, label));
    }

    private static LaunchInstance Server(string projectFile, LaunchOptions options, string logFolder)
    {
        const string label = "Server";

        var args = new List<string> { Quote(projectFile) };

        var url = MapUrl(options, listen: false);

        if (url.Length > 0)
            args.Add(url);

        args.Add("-server");
        args.Add($"-port={options.Port}");

        AddCommon(args, options, label, logFolder);

        return new LaunchInstance(label, true, args, LogFile(logFolder, label));
    }

    /// <summary>
    /// -ForceDPCVars=sg.ShadowQuality=0,... for a level, or null for Default.
    /// </summary>
    /// <remarks>
    /// ForceDPCVars rather than -ExecCmds="scalability 0": the device-profile cvars are set
    /// while the engine starts, before the first map loads, which is the point on a heavy
    /// map — ExecCmds only run on the first tick, after the load they were meant to speed up.
    /// The Force variant sets them at command-line priority, above what
    /// GameUserSettings.ini applies from the player's saved settings, so a project that
    /// calls ApplySettings on startup does not quietly put them back.
    /// </remarks>
    public static string? ScalabilityArgument(string level)
    {
        var index = Array.IndexOf(ScalabilityLevels.ToArray(), level);

        // Index 0 is Default; Low is quality level 0.
        if (index <= 0)
            return null;

        return "-ForceDPCVars=" + string.Join(",", ScalabilityGroups.Select(g => $"{g}={index - 1}"));
    }

    /// <summary>What every process gets, window or not.</summary>
    private static void AddCommon(List<string> args, LaunchOptions options, string label, string logFolder)
    {
        if (options.ShowLogConsole)
            args.Add("-log");

        if (options.NoSteam)
            args.Add("-nosteam");

        // Quotes inside the value would end the argument early; UE splits commands on commas.
        var exec = options.ExecCmds.Replace("\"", "").Trim();

        if (exec.Length > 0)
            args.Add($"-ExecCmds=\"{exec}\"");

        args.Add($"-abslog={Quote(LogFile(logFolder, label))}");

        var extra = options.ExtraArguments.Trim();

        if (extra.Length > 0)
            args.Add(extra);
    }

    /// <summary>"/Game/Maps/L_Arena?listen?game=/Script/X.Y?Name=A", or "" for the default map.</summary>
    private static string MapUrl(LaunchOptions options, bool listen)
    {
        var map = options.Map.Trim();

        if (map.Length == 0)
            return "";

        var url = map;

        if (listen)
            url += "?listen";

        if (options.GameMode.Trim() is { Length: > 0 } gameMode)
            url += $"?game={gameMode}";

        return url + options.UrlOptions.Trim();
    }

    /// <summary>
    /// Window rectangles for <paramref name="count"/> windows in a grid over the screen, each
    /// at the chosen resolution or shrunk to fit its cell with the same aspect ratio.
    /// Empty for one window, which Unreal centres by itself, or when tiling is off.
    /// </summary>
    public static IReadOnlyList<(int X, int Y, int W, int H)> Tiles(int count, LaunchOptions options, ScreenArea screen)
    {
        var tiles = new List<(int, int, int, int)>();

        if (!options.TileWindows || !options.Windowed || count < 2 || screen.Width <= 0 || screen.Height <= 0)
            return tiles;

        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (int)Math.Ceiling(count / (double)columns);

        var cellWidth = screen.Width / columns;
        var cellHeight = screen.Height / rows;

        // A window's title bar and frame sit outside -ResX/-ResY; leave room for them so
        // neighbouring windows do not overlap.
        const int frameWidth = 16;
        const int frameHeight = 40;

        var scale = Math.Min(
            1.0,
            Math.Min(
                (cellWidth - frameWidth) / (double)Math.Max(1, options.ResX),
                (cellHeight - frameHeight) / (double)Math.Max(1, options.ResY)));

        var width = Math.Max(320, (int)(options.ResX * scale));
        var height = Math.Max(240, (int)(options.ResY * scale));

        for (var i = 0; i < count; i++)
        {
            var column = i % columns;
            var row = i / columns;

            tiles.Add((screen.X + column * cellWidth, screen.Y + row * cellHeight, width, height));
        }

        return tiles;
    }

    /// <summary>
    /// For a C++ project, the editor module UnrealEditor.exe needs to load it, when it has
    /// not been built. Null for Blueprint-only projects and projects that are built.
    /// </summary>
    private static string? MissingEditorModule(string projectFile)
    {
        var directory = ProjectLoader.GetProjectDirectory(projectFile);

        if (!Directory.Exists(Path.Combine(directory, "Source")))
            return null;

        var binaries = Path.Combine(directory, "Binaries", "Win64");
        var module = $"UnrealEditor-{ProjectLoader.GetProjectName(projectFile)}.dll";

        try
        {
            // Any editor module will do: a project whose primary module is named differently
            // from the .uproject still has one, and that is all this is checking for.
            if (Directory.Exists(binaries) && Directory.EnumerateFiles(binaries, "UnrealEditor-*.dll").Any())
                return null;
        }
        catch (Exception)
        {
            return null;
        }

        return module;
    }

    private static string LogFile(string logFolder, string label)
        => Path.Combine(logFolder, label.Replace(' ', '_') + ".log");

    private static string Quote(string value) => $"\"{value}\"";
}
