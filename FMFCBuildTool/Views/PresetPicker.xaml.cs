using System.Windows.Controls;

namespace FMFCBuildTool.Views;

/// <summary>
/// Named-preset row shared by the Navigation and Lighting pages. DataContext is a
/// <see cref="ViewModels.CommandletPageViewModel"/>.
/// </summary>
public partial class PresetPicker : UserControl
{
    public PresetPicker()
    {
        InitializeComponent();
    }
}
