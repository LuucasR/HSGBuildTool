using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using FMFCBuildTool.Views;
using Xunit;

namespace FMFCBuildTool.Tests;

/// <summary>
/// That the Compiler page's XAML actually loads.
/// </summary>
/// <remarks>
/// A StaticResource that does not resolve is not a build error: it throws the first time
/// the page is rendered, which is the first time somebody opens the tab. This caught
/// exactly that — OptionCheck is a page-local style each page declares for itself, not a
/// theme resource, so borrowing the name from another page compiled fine and would have
/// crashed on click.
///
/// Scoped to the views this covers rather than every page in the app: the point is to
/// guard the resources these two use, not to build a harness for the whole shell.
/// </remarks>
public class CompilerPageLoadTests
{
    [Fact]
    public void The_compiler_page_and_its_results_panel_resolve_every_resource_they_use()
    {
        Sta.Run(() =>
        {
            WithApplicationResources();

            _ = new BlueprintResultsPanel();
            _ = new CompilerPage();

            return Task.CompletedTask;
        });
    }

    /// <summary>Stands in for App.xaml, which no test process ever runs.</summary>
    private static void WithApplicationResources()
    {
        var app = Application.Current ?? new Application();

        foreach (var dictionary in new[] { "Colors", "Typography", "Icons", "Controls" })
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/FMFCBuildTool;component/Resources/Theme/{dictionary}.xaml")
            });
        }

        app.Resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
        app.Resources["InverseBoolToVisibility"] = new InverseBoolToVisibilityConverter();
        app.Resources["StringToVisibility"] = new StringToVisibilityConverter();
        app.Resources["InverseBool"] = new InverseBoolConverter();
        app.Resources["Equals"] = new EqualsConverter();
        app.Resources["SeverityToBrush"] = new SeverityToBrushConverter();
    }
}
