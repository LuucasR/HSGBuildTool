using System.Windows;
using System.Windows.Controls;

namespace FMFCBuildTool.Views;

/// <summary>
/// Footer shared by the three build pages: command preview, validation, progress,
/// elapsed time, the two log shortcuts, Stop, and the primary Run button.
/// </summary>
/// <remarks>
/// DataContext is inherited from the page and must implement
/// <see cref="ViewModels.IBuildPage"/>.
/// </remarks>
public partial class BuildActionBar : UserControl
{
    /// <summary>Page-specific buttons stacked under Copy and Save .bat.</summary>
    public static readonly DependencyProperty ExtraActionsProperty =
        DependencyProperty.Register(nameof(ExtraActions), typeof(object), typeof(BuildActionBar), new PropertyMetadata(null));

    /// <summary>
    /// Page-specific tool buttons, placed before Log folder in the bottom row. Package
    /// puts "Output folder" here — the other two pages produce no artefact to open.
    /// </summary>
    public static readonly DependencyProperty ExtraToolButtonsProperty =
        DependencyProperty.Register(nameof(ExtraToolButtons), typeof(object), typeof(BuildActionBar), new PropertyMetadata(null));

    public BuildActionBar()
    {
        InitializeComponent();
    }

    public object? ExtraActions
    {
        get => GetValue(ExtraActionsProperty);
        set => SetValue(ExtraActionsProperty, value);
    }

    public object? ExtraToolButtons
    {
        get => GetValue(ExtraToolButtonsProperty);
        set => SetValue(ExtraToolButtonsProperty, value);
    }
}
