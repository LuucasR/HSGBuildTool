using System.Diagnostics;

namespace FMFCBuildTool.Services;

public class ProcessRunner
{
    private Process? CurrentProcess;

    public event Action<string>? OutputReceived;

    public event Action<int>? ProcessExited;

    public async Task<int> RunAsync(string exe, string arguments, string workingDirectory = "")
    {
        using var process = new Process();

        CurrentProcess = process;

        process.StartInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,

            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            CreateNoWindow = true
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                OutputReceived?.Invoke(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                OutputReceived?.Invoke("[ERROR] " + e.Data);
        };

        process.Start();

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        ProcessExited?.Invoke(process.ExitCode);

        CurrentProcess = null;

        return process.ExitCode;
    }


    public void Cancel()
    {
        if (CurrentProcess == null)
            return;

        try
        {
            if (!CurrentProcess.HasExited)
                CurrentProcess.Kill(true);
        }
        catch
        {
        }
    }
}