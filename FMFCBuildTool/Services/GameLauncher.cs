using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>One game or server process the Launch page started, as its list shows it.</summary>
public sealed class GameInstance : ObservableObject
{
    private string _state = "Starting";
    private bool _isRunning = true;

    public GameInstance(LaunchInstance launch)
    {
        Launch = launch;
    }

    public LaunchInstance Launch { get; }

    public string Label => Launch.Label;

    public string LogFile => Launch.LogFile;

    public int? ProcessId { get; internal set; }

    internal Process? Process { get; set; }

    public string State
    {
        get => _state;
        internal set => SetProperty(ref _state, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        internal set => SetProperty(ref _isRunning, value);
    }
}

/// <summary>
/// Starts and tracks the game processes the Launch page runs: several at once, alongside
/// whatever build is going.
/// </summary>
/// <remarks>
/// Deliberately not <see cref="ProcessRunner"/>. That runner is single-flight, and every
/// build page checks it before enabling its button — a game left open would block every
/// build until someone closed it. It also waits for its process to exit, which for a game
/// is whenever the tester is done.
///
/// Output comes from each instance's -abslog file rather than stdout: UnrealEditor.exe is
/// a GUI-subsystem exe, and what it sends to a redirected stdout depends on switches the
/// packaged game would not have. The file is what the engine writes regardless.
/// </remarks>
public sealed class GameLauncher : IDisposable
{
    /// <summary>How long a server gets to start listening before its clients try to connect.</summary>
    public static readonly TimeSpan ServerHeadStart = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan TailInterval = TimeSpan.FromMilliseconds(250);

    private readonly OutputService _output;
    private CancellationTokenSource _stopping = new();

    public GameLauncher(OutputService output)
    {
        _output = output;
    }

    /// <summary>The instances of the last launch. Changed on the UI thread only.</summary>
    public ObservableCollection<GameInstance> Instances { get; } = new();

    public bool IsRunning => Instances.Any(i => i.IsRunning);

    /// <summary>An instance started or exited. May fire on a worker thread.</summary>
    public event Action? RunningChanged;

    /// <summary>
    /// Starts every instance, servers first. Call on the UI thread: it replaces
    /// <see cref="Instances"/>, which the page's list is bound to.
    /// </summary>
    public async Task LaunchAsync(string exe, string workingDirectory, System.Collections.Generic.IReadOnlyList<LaunchInstance> launches)
    {
        if (IsRunning)
            throw new InvalidOperationException("Instances from the last launch are still running. Stop them first.");

        _stopping.Dispose();
        _stopping = new CancellationTokenSource();

        var token = _stopping.Token;

        Instances.Clear();

        var previousWasServer = false;

        foreach (var launch in launches)
        {
            if (token.IsCancellationRequested)
                break;

            if (previousWasServer && !launch.IsServer)
            {
                try
                {
                    await Task.Delay(ServerHeadStart, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            var instance = new GameInstance(launch);

            Instances.Add(instance);

            Start(exe, workingDirectory, instance, token);

            previousWasServer = launch.IsServer;
        }

        RunningChanged?.Invoke();
    }

    /// <summary>Kills every running instance and whatever it started.</summary>
    public void StopAll()
    {
        _stopping.Cancel();

        foreach (var instance in Instances.ToArray())
        {
            try
            {
                if (instance.Process is { HasExited: false } process)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited between the check and the kill.
            }
            catch (Exception ex)
            {
                _output.WriteTool($"Could not stop {instance.Label}: {ex.Message}", LogSeverity.Warning);
            }
        }
    }

    public void Dispose() => StopAll();

    private void Start(string exe, string workingDirectory, GameInstance instance, CancellationToken token)
    {
        var launch = instance.Launch;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(launch.LogFile)!);

            // The previous run's file would otherwise be tailed from its first line.
            if (File.Exists(launch.LogFile))
                File.Delete(launch.LogFile);
        }
        catch (Exception ex)
        {
            _output.WriteTool($"{launch.Label}: could not reset {launch.LogFile}: {ex.Message}", LogSeverity.Warning);
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = LaunchBuilder.ToCommandLine(launch.Arguments),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false
            },
            EnableRaisingEvents = true
        };

        process.Exited += (_, _) =>
        {
            int code;

            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = -1;
            }

            instance.State = code == 0 ? "Exited" : $"Exited ({code})";
            instance.IsRunning = false;

            _output.WriteTool(
                $"{launch.Label} exited with code {code}.",
                code == 0 || token.IsCancellationRequested ? LogSeverity.Info : LogSeverity.Warning);

            RunningChanged?.Invoke();
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            instance.State = "Failed to start";
            instance.IsRunning = false;

            _output.WriteTool($"{launch.Label}: could not start {exe}: {ex.Message}", LogSeverity.Error);

            process.Dispose();
            return;
        }

        instance.Process = process;
        instance.ProcessId = process.Id;
        instance.State = "Running";

        _output.WriteTool($"{launch.Label} started (pid {process.Id}): {LaunchBuilder.ToCommandLine(launch.Arguments)}");

        _ = Task.Run(() => TailAsync(instance, process));
    }

    /// <summary>
    /// Follows the instance's log file into Output until the process has exited and the
    /// file has been read to the end.
    /// </summary>
    private async Task TailAsync(GameInstance instance, Process process)
    {
        var path = instance.LogFile;
        var label = instance.Label;
        var pending = new StringBuilder();
        var buffer = new char[8192];

        try
        {
            // The engine creates the file a moment after the process starts.
            while (!File.Exists(path))
            {
                if (process.HasExited)
                    return;

                await Task.Delay(TailInterval);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (true)
            {
                var exited = process.HasExited;
                var read = await reader.ReadAsync(buffer, 0, buffer.Length);

                if (read > 0)
                {
                    pending.Append(buffer, 0, read);
                    Flush(pending, label, final: false);
                    continue;
                }

                if (exited)
                {
                    Flush(pending, label, final: true);
                    return;
                }

                await Task.Delay(TailInterval);
            }
        }
        catch (Exception ex)
        {
            _output.WriteTool($"{label}: stopped following {path}: {ex.Message}", LogSeverity.Warning);
        }
    }

    /// <summary>
    /// Writes out every complete line. A line the engine is still in the middle of writing
    /// stays in <paramref name="pending"/> until its newline arrives, unless this is the end.
    /// </summary>
    private void Flush(StringBuilder pending, string label, bool final)
    {
        var text = pending.ToString();
        var lastNewline = text.LastIndexOf('\n');

        string complete;

        if (final)
        {
            complete = text;
            pending.Clear();
        }
        else if (lastNewline < 0)
        {
            return;
        }
        else
        {
            complete = text[..lastNewline];
            pending.Remove(0, lastNewline + 1);
        }

        foreach (var line in complete.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (!string.IsNullOrWhiteSpace(trimmed))
                _output.Write(trimmed, label);
        }
    }
}
