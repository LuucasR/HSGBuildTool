using System.Windows;

namespace FMFCBuildTool.Views;

/// <summary>
/// Shell window. All behaviour lives in <see cref="ViewModels.MainViewModel"/>; this
/// file used to hold the navigation logic, three duplicate ProcessExited handlers and
/// the log rendering.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
