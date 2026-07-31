using System.Windows.Controls;

namespace FMFCBuildTool.Views;

/// <summary>
/// Map picker shared by Package, Navigation and Lighting.
/// DataContext is a <see cref="ViewModels.MapSelectionViewModel"/>.
/// </summary>
public partial class MapListControl : UserControl
{
    public MapListControl()
    {
        InitializeComponent();
    }
}
