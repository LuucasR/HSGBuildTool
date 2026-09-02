using System.Windows.Controls;

namespace FMFCBuildTool.Views;

/// <summary>
/// Per-map outcome of a commandlet run, with "retry failed". DataContext is a
/// <see cref="ViewModels.CommandletPageViewModel"/>; hides itself until there is
/// something to show.
/// </summary>
public partial class MapResultsPanel : UserControl
{
    public MapResultsPanel()
    {
        InitializeComponent();
    }
}
