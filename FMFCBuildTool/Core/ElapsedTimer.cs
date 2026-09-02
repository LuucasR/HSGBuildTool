using System;
using System.Diagnostics;
using System.Windows.Threading;

namespace FMFCBuildTool.Core;

/// <summary>
/// A ticking "hh:mm:ss" clock for the duration of a build.
/// </summary>
/// <remarks>
/// Package built this by hand from a Stopwatch plus a DispatcherTimer, while Navigation
/// and Lighting had only the Stopwatch — so on those two pages the elapsed time appeared
/// once, in the closing summary, and never while the build was actually running. One
/// implementation now serves all three.
///
/// <see cref="Stop"/> deliberately keeps the last value on screen: the old Package code
/// blanked it in its finally block, which threw away the one duration you most want to
/// read — the final one.
/// </remarks>
public sealed class ElapsedTimer : ObservableObject
{
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string _text = "";

    public ElapsedTimer()
    {
        _timer.Tick += (_, _) => Text = Format(_stopwatch.Elapsed);
    }

    /// <summary>The displayed value: "hh:mm:ss" while running or stopped, empty when reset.</summary>
    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>Elapsed time formatted the same way as <see cref="Text"/>, for log lines.</summary>
    public string Formatted => Format(_stopwatch.Elapsed);

    public bool IsRunning => _stopwatch.IsRunning;

    public void Restart()
    {
        _stopwatch.Restart();

        Text = Format(TimeSpan.Zero);

        _timer.Start();
    }

    /// <summary>Stops ticking and leaves the final duration visible.</summary>
    public void Stop()
    {
        _timer.Stop();
        _stopwatch.Stop();

        Text = Format(_stopwatch.Elapsed);
    }

    /// <summary>Stops and clears the display.</summary>
    public void Reset()
    {
        _timer.Stop();
        _stopwatch.Reset();

        Text = "";
    }

    private static string Format(TimeSpan value) => value.ToString(@"hh\:mm\:ss");
}
