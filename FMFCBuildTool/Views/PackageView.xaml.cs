using System.Windows.Controls;

namespace FMFCBuildTool.Views;

/// <summary>
/// BuildCookRun page. All state and behaviour live in
/// <see cref="ViewModels.PackageViewModel"/>; this file used to be 443 lines of
/// event handlers and manual control-to-model copying.
/// </summary>
public partial class PackageView : UserControl
{
    public PackageView()
    {
        InitializeComponent();
    }
}
