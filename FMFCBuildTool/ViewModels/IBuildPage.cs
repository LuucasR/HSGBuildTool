using System.Threading.Tasks;
using System.Windows.Input;
using FMFCBuildTool.Models;

namespace FMFCBuildTool.ViewModels;

/// <summary>
/// What every build page (Package, Navigation, Lighting) exposes to the shared
/// <see cref="Views.BuildActionBar"/> and to the build queue.
/// </summary>
/// <remarks>
/// The three pages carried three near-identical footers that had already drifted apart —
/// Package grew an elapsed clock and a "Save .bat" button that the other two never got,
/// and neither of the others could open the log of the run they had just produced. One
/// bar now renders all three, and this interface is what keeps them bindable by the same
/// names. XAML binds by name and would fail silently on a typo; the compiler will not.
/// </remarks>
public interface IBuildPage
{
    /// <summary>"package", "nav" or "lighting". Identifies the page in history and the queue.</summary>
    string Kind { get; }

    /// <summary>Verb on the primary button, e.g. "BUILD NAVIGATION".</summary>
    string RunButtonText { get; }

    ICommand RunCommand { get; }

    /// <summary>Kills the running process tree and cancels the rest of the run.</summary>
    ICommand StopCommand { get; }

    ICommand CopyCommandLineCommand { get; }

    ICommand SaveBatchFileCommand { get; }

    ICommand OpenLogFileCommand { get; }

    ICommand OpenLogFolderCommand { get; }

    bool IsRunning { get; }

    /// <summary>False while validation is failing or another page is already building.</summary>
    bool CanRun { get; }

    /// <summary>
    /// True for pages that cannot report real progress. Package runs one opaque RunUAT
    /// invocation; the commandlet pages know how many maps are left.
    /// </summary>
    bool IsProgressIndeterminate { get; }

    /// <summary>0-100. Ignored while <see cref="IsProgressIndeterminate"/> is true.</summary>
    double Progress { get; }

    string StatusText { get; }

    /// <summary>"hh:mm:ss" while a build runs, and the final duration once it ends.</summary>
    string ElapsedText { get; }

    /// <summary>"~12:34 last time", from build history, or empty with nothing to go on.</summary>
    string EstimateText { get; }

    string CommandPreview { get; }

    string ValidationMessage { get; }

    bool HasValidationMessage { get; }

    /// <summary>Outcome of the most recent run in this session, or null before the first.</summary>
    BuildOutcome? LastOutcome { get; }

    /// <summary>
    /// Runs the build and completes when it is finished. The queue needs this: the command
    /// is fire-and-forget, so a chained run had no way to know when to start the next step.
    /// </summary>
    Task RunAsync();
}
