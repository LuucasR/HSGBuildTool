using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FMFCBuildTool.Core;
using Xunit;

namespace FMFCBuildTool.Tests;

public class ElapsedTimerTests
{
    /// <summary>Nothing to show before a run has started.</summary>
    [Fact]
    public void Starts_empty()
    {
        Sta.Run(() =>
        {
            Assert.Equal("", new ElapsedTimer().Text);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Shows_zero_as_soon_as_it_starts()
    {
        Sta.Run(() =>
        {
            var timer = new ElapsedTimer();

            timer.Restart();

            // The first tick is a second away, so the initial value has to be set up front
            // or the bar sits blank for the first second of every build.
            Assert.Equal("00:00:00", timer.Text);
            Assert.True(timer.IsRunning);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The whole point of Stop over Reset: a finished build's duration stays readable.
    /// The old Package code blanked it, so the final number vanished the moment it
    /// became interesting.
    /// </summary>
    [Fact]
    public void Stop_keeps_the_final_duration_and_reset_clears_it()
    {
        Sta.Run(() =>
        {
            var timer = new ElapsedTimer();

            timer.Restart();
            timer.Stop();

            Assert.Matches(new Regex(@"^\d{2}:\d{2}:\d{2}$"), timer.Text);
            Assert.False(timer.IsRunning);

            timer.Reset();

            Assert.Equal("", timer.Text);

            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Restart_clears_a_previous_run()
    {
        Sta.Run(() =>
        {
            var timer = new ElapsedTimer();

            timer.Restart();
            timer.Stop();
            timer.Restart();

            Assert.Equal("00:00:00", timer.Text);

            return Task.CompletedTask;
        });
    }

    /// <summary>Text has to raise PropertyChanged or the bar never updates.</summary>
    [Fact]
    public void Notifies_when_the_text_changes()
    {
        Sta.Run(() =>
        {
            var timer = new ElapsedTimer();
            var notified = false;

            timer.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(ElapsedTimer.Text);

            timer.Restart();

            Assert.True(notified);

            return Task.CompletedTask;
        });
    }
}
