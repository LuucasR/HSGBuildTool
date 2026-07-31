using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FMFCBuildTool.Services;

/// <summary>
/// Runs one external build process at a time and streams its output.
/// </summary>
/// <remarks>
/// Fixes two problems in the previous version: the process was wrapped in
/// <c>using var</c> while a field still referenced it, so <c>Cancel()</c> could touch
/// a disposed object (swallowed by an empty catch); and nothing stopped a nav build
/// from starting on top of a running package build, since both pages shared the
/// same runner instance. <see cref="IsRunning"/> now makes that state observable.
/// </remarks>
public sealed class ProcessRunner
{
    private readonly object _gate = new();

    private Process? _current;
    private bool _isRunning;

    public event Action<string>? OutputReceived;

    /// <summary>Fired once per run with the exit code. Wire it up exactly once.</summary>
    public event Action<int>? ProcessExited;

    public event Action? RunningChanged;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _isRunning;
        }
    }

    /// <summary>What is currently running, for the "a build is already in progress" message.</summary>
    public string CurrentDescription { get; private set; } = "";

    public async Task<int> RunAsync(
        string exe,
        string arguments,
        string workingDirectory = "",
        string description = "",
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_isRunning)
                throw new InvalidOperationException($"A build is already running ({CurrentDescription}). Cancel it first.");

            _isRunning = true;
        }

        CurrentDescription = string.IsNullOrWhiteSpace(description) ? exe : description;
        RunningChanged?.Invoke();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,

                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,

                CreateNoWindow = true
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                OutputReceived?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                OutputReceived?.Invoke(e.Data);
        };

        try
        {
            lock (_gate)
                _current = process;

            process.Start();

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await using (cancellationToken.Register(() => KillCurrent()))
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }

            var exitCode = process.ExitCode;

            ProcessExited?.Invoke(exitCode);

            return exitCode;
        }
        finally
        {
            lock (_gate)
            {
                _current = null;
                _isRunning = false;
            }

            CurrentDescription = "";

            process.Dispose();

            RunningChanged?.Invoke();
        }
    }

    /// <summary>Kills the running process and its children (UAT spawns UBT, the cooker, …).</summary>
    public void Cancel() => KillCurrent();

    private void KillCurrent()
    {
        Process? process;

        lock (_gate)
            process = _current;

        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill — nothing to do.
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"Could not cancel the running process: {ex.Message}");
        }
    }
}
