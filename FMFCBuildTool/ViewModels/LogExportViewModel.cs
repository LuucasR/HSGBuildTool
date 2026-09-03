using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Input;
using FMFCBuildTool.Core;
using FMFCBuildTool.Models;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.ViewModels;

/// <summary>One entry in the export menu.</summary>
public sealed record LogExportOption(string Label, string Description, LogExportScope Scope);

/// <summary>
/// Writes the log out to a file, keeping only the severities you pick.
/// </summary>
/// <remarks>
/// Owned by the log panel and by every build page, so the same menu sits in the log
/// toolbar and in the footer of Package, Navigation and Lighting. One class rather than
/// three copies of a SaveFileDialog, which is how the three build footers drifted apart
/// in the first place.
///
/// The severity chips and the search box deliberately do not feed into this: filtering the
/// view to read through the noise should not silently change what a later export contains.
/// </remarks>
public sealed class LogExportViewModel
{
    private readonly OutputService _output;

    public LogExportViewModel(OutputService output)
    {
        _output = output;

        ExportCommand = new RelayCommand(Export);
    }

    public IReadOnlyList<LogExportOption> Options { get; } = new[]
    {
        new LogExportOption("Everything", "Every line, as the log file already has it", LogExportScope.Everything),
        new LogExportOption("Warnings + errors", "Both, and nothing else", LogExportScope.WarningsAndErrors),
        new LogExportOption("Errors only", "Just what broke", LogExportScope.ErrorsOnly),
        new LogExportOption("Warnings only", "Warnings without the errors around them", LogExportScope.WarningsOnly)
    };

    /// <summary>Takes a <see cref="LogExportOption"/> as its parameter.</summary>
    public ICommand ExportCommand { get; }

    private void Export(object? parameter)
    {
        if (parameter is not LogExportOption option)
            return;

        var matches = _output.CountFor(option.Scope);

        if (matches == 0)
        {
            // Writing an empty file and saying nothing is the one outcome nobody wants.
            _output.WriteTool(
                $"Nothing to export: no lines match \"{option.Label}\".",
                LogSeverity.Warning);

            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Log file (*.log)|*.log|Text file (*.txt)|*.txt",
            FileName = SuggestedName(option.Scope),
            InitialDirectory = Directory.Exists(_output.LogDirectory) ? _output.LogDirectory : ""
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var written = _output.WriteFiltered(dialog.FileName, option.Scope);

            _output.WriteTool($"Exported {written} line(s) to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            _output.WriteTool($"Could not export the log: {ex.Message}", LogSeverity.Error);
        }
    }

    /// <summary>
    /// Names the export after the run it came from, so a folder of them stays readable.
    /// Falls back to a timestamp before the first build, when there is no log file yet.
    /// </summary>
    private string SuggestedName(LogExportScope scope)
    {
        var stem = string.IsNullOrEmpty(_output.CurrentLogFile)
            ? $"fmfc-{DateTime.Now:yyyyMMdd-HHmmss}"
            : Path.GetFileNameWithoutExtension(_output.CurrentLogFile);

        return $"{stem}-{scope.FileSuffix()}.log";
    }
}
