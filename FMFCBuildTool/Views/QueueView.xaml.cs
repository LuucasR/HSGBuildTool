using System.Windows.Controls;

namespace FMFCBuildTool.Views;

/// <summary>
/// The build queue page. DataContext is a <see cref="ViewModels.BuildQueueViewModel"/>.
/// </summary>
public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
    }
}
