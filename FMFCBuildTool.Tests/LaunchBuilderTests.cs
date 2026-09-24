using System.IO;
using System.Linq;
using FMFCBuildTool.Services;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// The Launch page's command lines. Which process gets -server, who gets ?listen, and
/// where the clients connect are the parts that silently produce a game nobody can join.
/// </summary>
public class LaunchBuilderTests
{
    private const string ProjectFile = @"D:\Proj\FMFC.uproject";
    private const string Map = "/Game/Maps/L_Arena";

    private static readonly ScreenArea Screen = new(0, 0, 1920, 1040);

    private static LaunchOptions Options(
        LaunchMode mode = LaunchMode.Standalone,
        string map = Map,
        int clients = 2,
        int port = 7777,
        string address = "127.0.0.1",
        bool windowed = true,
        bool tile = true,
        string gameMode = "",
        string url = "",
        string rhi = "Default",
        bool noSound = false,
        bool log = false,
        string exec = "",
        bool noSteam = false,
        string extra = "")
        => new(mode, map, clients, port, address, windowed, 1280, 720, tile, gameMode, url, rhi, noSound, log, exec, noSteam, extra);

    [Fact]
    public void Standalone_is_one_game_process_on_the_map()
    {
        var instances = LaunchBuilder.Build(ProjectFile, Options(), Screen);

        var game = Assert.Single(instances);

        Assert.Equal("Game", game.Label);
        Assert.Equal($"\"{ProjectFile}\"", game.Arguments[0]);
        Assert.Equal(Map, game.Arguments[1]);
        Assert.Contains("-game", game.Arguments);
        Assert.DoesNotContain("-server", game.Arguments);
        Assert.DoesNotContain(game.Arguments, a => a.StartsWith("-port="));
    }

    [Fact]
    public void The_project_default_map_puts_no_url_on_the_command_line()
    {
        var game = LaunchBuilder.Build(ProjectFile, Options(map: ""), Screen).Single();

        Assert.Equal("-game", game.Arguments[1]);
    }

    [Fact]
    public void A_listen_server_hosts_on_the_map_and_its_clients_connect_to_localhost()
    {
        var instances = LaunchBuilder.Build(ProjectFile, Options(LaunchMode.ListenServer, clients: 2, port: 7800), Screen);

        Assert.Equal(new[] { "Host", "Client 1", "Client 2" }, instances.Select(i => i.Label));

        Assert.True(instances[0].IsServer);
        Assert.Equal($"{Map}?listen", instances[0].Arguments[1]);
        Assert.Contains("-game", instances[0].Arguments);
        Assert.Contains("-port=7800", instances[0].Arguments);

        Assert.All(instances.Skip(1), c =>
        {
            Assert.False(c.IsServer);
            Assert.Equal("127.0.0.1:7800", c.Arguments[1]);
            Assert.Contains("-game", c.Arguments);
        });
    }

    [Fact]
    public void A_dedicated_server_runs_with_server_and_no_window()
    {
        var instances = LaunchBuilder.Build(ProjectFile, Options(LaunchMode.DedicatedServer, clients: 3), Screen);

        Assert.Equal(4, instances.Count);

        var server = instances[0];

        Assert.Equal("Server", server.Label);
        Assert.True(server.IsServer);
        Assert.Contains("-server", server.Arguments);
        Assert.DoesNotContain("-game", server.Arguments);
        Assert.DoesNotContain("-windowed", server.Arguments);
        Assert.DoesNotContain(server.Arguments, a => a.StartsWith("-ResX"));
    }

    [Fact]
    public void Client_only_connects_to_the_given_address_and_starts_no_server()
    {
        var instances = LaunchBuilder.Build(ProjectFile, Options(LaunchMode.ClientOnly, clients: 1, address: "10.0.0.5", url: "?Name=QA"), Screen);

        var client = Assert.Single(instances);

        Assert.Equal("10.0.0.5:7777?Name=QA", client.Arguments[1]);
    }

    [Fact]
    public void Dedicated_server_only_starts_just_the_server()
    {
        var instances = LaunchBuilder.Build(ProjectFile, Options(LaunchMode.DedicatedServerOnly), Screen);

        Assert.Equal("Server", Assert.Single(instances).Label);
    }

    [Fact]
    public void GameMode_and_url_options_go_on_the_map_url()
    {
        var host = LaunchBuilder.Build(
            ProjectFile,
            Options(LaunchMode.ListenServer, gameMode: "/Script/FMFC.ArenaMode", url: "?Name=Host"),
            Screen)[0];

        Assert.Equal($"{Map}?listen?game=/Script/FMFC.ArenaMode?Name=Host", host.Arguments[1]);
    }

    [Fact]
    public void Every_instance_logs_to_its_own_file()
    {
        var instances = LaunchBuilder.Build(ProjectFile, Options(LaunchMode.DedicatedServer, clients: 2), Screen);

        var logs = instances.Select(i => i.LogFile).ToList();

        Assert.Equal(logs.Count, logs.Distinct().Count());
        Assert.All(instances, i => Assert.Contains($"-abslog=\"{i.LogFile}\"", i.Arguments));
        Assert.Equal(Path.Combine(@"D:\Proj", "Saved", "Logs", "Launch", "Client_1.log"), instances[1].LogFile);
    }

    [Fact]
    public void Several_windows_are_tiled_without_overlapping()
    {
        var instances = LaunchBuilder.Build(ProjectFile, Options(LaunchMode.ListenServer, clients: 3), Screen);

        var positions = instances
            .Select(i => (
                X: i.Arguments.Single(a => a.StartsWith("-WinX=")),
                Y: i.Arguments.Single(a => a.StartsWith("-WinY="))))
            .ToList();

        Assert.Equal(4, positions.Distinct().Count());

        // Four 1280x720 windows do not fit in two columns of 960, so they are shrunk.
        Assert.All(instances, i => Assert.DoesNotContain("-ResX=1280", i.Arguments));
    }

    [Fact]
    public void One_window_is_left_for_unreal_to_centre()
    {
        var game = LaunchBuilder.Build(ProjectFile, Options(), Screen).Single();

        Assert.Contains("-ResX=1280", game.Arguments);
        Assert.DoesNotContain(game.Arguments, a => a.StartsWith("-WinX="));
    }

    [Fact]
    public void Fullscreen_replaces_every_window_switch()
    {
        var game = LaunchBuilder.Build(ProjectFile, Options(windowed: false), Screen).Single();

        Assert.Contains("-fullscreen", game.Arguments);
        Assert.DoesNotContain("-windowed", game.Arguments);
        Assert.DoesNotContain(game.Arguments, a => a.StartsWith("-ResX="));
    }

    [Fact]
    public void Simulation_switches_are_passed_through()
    {
        var game = LaunchBuilder.Build(
            ProjectFile,
            Options(rhi: "DX12", noSound: true, log: true, noSteam: true, exec: "stat fps, \"stat unit\"", extra: "-FPS=60"),
            Screen).Single();

        Assert.Contains("-dx12", game.Arguments);
        Assert.Contains("-nosound", game.Arguments);
        Assert.Contains("-log", game.Arguments);
        Assert.Contains("-nosteam", game.Arguments);
        Assert.Contains("-ExecCmds=\"stat fps, stat unit\"", game.Arguments);
        Assert.Equal("-FPS=60", game.Arguments[^1]);
    }

    [Fact]
    public void A_listen_server_without_a_map_is_refused()
    {
        var problems = LaunchBuilder.Validate(ProjectFile, null, Options(LaunchMode.ListenServer, map: ""));

        // The project file does not exist here, so this stops at "open a project" —
        // which is itself the check that nothing launches without one.
        Assert.Contains(problems, p => p.Contains("project"));
    }

    [Fact]
    public void Url_options_must_start_with_a_question_mark()
    {
        var project = TempProject();

        var problems = LaunchBuilder.Validate(project, null, Options(url: "Name=QA"));

        Assert.Contains(problems, p => p.Contains("start with ?"));
    }

    [Fact]
    public void A_listen_server_needs_a_map()
    {
        var project = TempProject();

        var problems = LaunchBuilder.Validate(project, null, Options(LaunchMode.ListenServer, map: ""));

        Assert.Contains(problems, p => p.Contains("?listen"));
    }

    [Fact]
    public void Valid_options_have_no_problems()
    {
        var project = TempProject();

        Assert.Empty(LaunchBuilder.Validate(project, null, Options(LaunchMode.DedicatedServer)));
    }

    private static string TempProject()
    {
        var directory = Directory.CreateTempSubdirectory("fmfc-launch-").FullName;
        var project = Path.Combine(directory, "Test.uproject");

        File.WriteAllText(project, "{ \"EngineAssociation\": \"5.4\" }");

        return project;
    }
}
