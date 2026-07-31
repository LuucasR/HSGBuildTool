using System.Windows;
using System.Windows.Threading;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;
using FMFCBuildTool.ViewModels;
using FMFCBuildTool.Views;

namespace FMFCBuildTool;

public partial class App : Application
{
    private MainViewModel? _main;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// Composition root. Services used to be constructed ad hoc inside the views —
    /// two of them each loaded their own copy of the config and saved over each other.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configService = new ConfigService();
        var output = new OutputService(configService.LogDirectory);

        configService.Error += message => output.WriteTool(message, LogSeverity.Error);

        var config = configService.Load();

        output.PruneLogs(config.LogRetentionDays);

        var runner = new ProcessRunner();
        var context = new BuildContext();

        runner.OutputReceived += output.Write;

        runner.ProcessExited += code => output.WriteTool(
            $"Process exited with code {code}.",
            code == 0 ? LogSeverity.Info : LogSeverity.Error);

        _main = new MainViewModel(config, configService, output, runner, context);

        var window = new MainWindow { DataContext = _main };

        MainWindow = window;

        window.Closing += (_, _) => _main.Shutdown();

        window.Show();

        _ = _main.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Save();

        base.OnExit(e);
    }

    /// <summary>
    /// Anything unhandled goes to the log panel and keeps the app alive, rather than
    /// tearing down a window that may have a build running.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _main?.Output.WriteTool($"Unexpected error: {e.Exception.Message}", LogSeverity.Error);

        e.Handled = true;
    }
}
