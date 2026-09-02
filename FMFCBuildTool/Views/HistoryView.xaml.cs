using System.Windows.Controls;

namespace FMFCBuildTool.Views;

/// <summary>
/// The build history page. DataContext is a <see cref="ViewModels.HistoryViewModel"/>.
/// </summary>
public partial class HistoryView : UserControl
{
    public HistoryView()
    {
        InitializeComponent();
    }
}
