using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FMFCBuildTool.Tests;

/// <summary>
/// Runs a test body on an STA thread with a running Dispatcher.
/// </summary>
/// <remarks>
/// <see cref="ViewModels.MapSelectionViewModel"/> is built on WPF's ICollectionView,
/// which only allows its source collection to be mutated from the thread that created
/// it. In the app that is guaranteed by WPF's DispatcherSynchronizationContext; here it
/// has to be set up by hand, and the dispatcher has to be pumping so that continuations
/// after an await can get back onto the right thread.
/// </remarks>
internal static class Sta
{
    public static void Run(Func<Task> body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;

            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw new InvalidOperationException("STA test body failed: " + failure.Message, failure);
    }
}
