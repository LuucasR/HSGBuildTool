using System;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.Services;

/// <summary>
/// Tells the user a build has finished when they are not looking at the tool.
/// </summary>
/// <remarks>
/// A cook runs for twenty minutes and nobody watches it, so the only thing that made the
/// end of a build visible was going back and checking. This plays a sound and flashes the
/// taskbar button — and only when the window is not already in the foreground, because a
/// notification about something you are staring at is just noise.
/// </remarks>
public sealed class NotificationService
{
    private const uint FlashTray = 0x00000002;
    private const uint FlashTimerUntilForeground = 0x0000000C;

    private readonly AppConfig _config;

    public NotificationService(AppConfig config)
    {
        _config = config;
    }

    public void Notify(BuildOutcome outcome)
    {
        if (!_config.NotifyOnFinish)
            return;

        var window = Application.Current?.MainWindow;

        if (window is null)
            return;

        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero || IsForeground(handle))
            return;

        Play(outcome);
        Flash(handle);
    }

    private static void Play(BuildOutcome outcome)
    {
        try
        {
            if (outcome == BuildOutcome.Succeeded)
                SystemSounds.Asterisk.Play();
            else
                SystemSounds.Hand.Play();
        }
        catch
        {
            // No audio device, or sounds disabled system-wide. Not worth reporting.
        }
    }

    /// <summary>Flashes until the user looks at the window, then stops on its own.</summary>
    private static void Flash(IntPtr handle)
    {
        try
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = handle,
                dwFlags = FlashTray | FlashTimerUntilForeground,
                uCount = uint.MaxValue,
                dwTimeout = 0
            };

            FlashWindowEx(ref info);
        }
        catch
        {
        }
    }

    private static bool IsForeground(IntPtr handle) => GetForegroundWindow() == handle;

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
}
