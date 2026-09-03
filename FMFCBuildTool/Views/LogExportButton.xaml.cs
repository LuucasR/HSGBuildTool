using System.Windows;
using System.Windows.Controls;
using FMFCBuildTool.ViewModels;

namespace FMFCBuildTool.Views;

/// <summary>
/// "Export" plus its severity menu. DataContext is a <see cref="LogExportViewModel"/>.
/// </summary>
public partial class LogExportButton : UserControl
{
    public LogExportButton()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Closes the menu, then exports. StaysOpen=False only closes the popup on a click
    /// *outside* it, so picking an option would otherwise leave the menu hanging open
    /// behind the save dialog.
    /// </summary>
    private void OnOptionClicked(object sender, RoutedEventArgs e)
    {
        Toggle.IsChecked = false;

        if (sender is Button { DataContext: LogExportOption option } &&
            DataContext is LogExportViewModel export)
        {
            export.ExportCommand.Execute(option);
        }
    }
}
