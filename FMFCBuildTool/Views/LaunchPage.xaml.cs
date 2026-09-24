using System.Windows.Controls;
using System.Windows.Input;

namespace FMFCBuildTool.Views;

/// <summary>Standalone launch page. DataContext is a <see cref="ViewModels.LaunchViewModel"/>.</summary>
public partial class LaunchPage : UserControl
{
    public LaunchPage()
    {
        InitializeComponent();

        // The saved map can be hundreds of rows down; show it rather than the top of the list.
        Loaded += (_, _) => ScrollToSelection();
    }

    private void OnMapSelectionChanged(object sender, SelectionChangedEventArgs e) => ScrollToSelection();

    private void ScrollToSelection()
    {
        if (MapList.SelectedItem is { } item)
            MapList.ScrollIntoView(item);
    }

    /// <summary>
    /// Down from the search box goes into the list, and Enter picks the first match, so a
    /// map can be found and chosen without the mouse.
    /// </summary>
    private void OnMapSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (MapList.Items.Count == 0)
            return;

        if (e.Key == Key.Down)
        {
            var index = MapList.SelectedIndex >= 0 ? MapList.SelectedIndex : 0;

            MapList.UpdateLayout();

            if (MapList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container)
                container.Focus();

            e.Handled = true;
        }
        else if (e.Key == Key.Enter && DataContext is ViewModels.LaunchViewModel viewModel)
        {
            // The binding waits 150 ms after the last key; Enter straight after typing
            // should search for what is in the box, not what was there a moment ago.
            MapSearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            if (viewModel.FirstSearchMatch is { } match)
                viewModel.Map = match;

            e.Handled = true;
        }
    }
}
