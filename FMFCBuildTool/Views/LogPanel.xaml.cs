using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FMFCBuildTool.ViewModels;

namespace FMFCBuildTool.Views;

/// <summary>
/// The log surface, used both by the shell's bottom dock and the full Output page.
/// One control and one view-model, so both always show the same thing.
/// </summary>
public partial class LogPanel : UserControl
{
    private OutputViewModel? _viewModel;

    public LogPanel()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        // The view-model outlives this control, so the handler must come off again —
        // the old OutputView subscribed in its constructor and never unsubscribed,
        // leaking a live renderer on every visit to the Output tab.
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) => Attach(DataContext as OutputViewModel);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        Attach(e.NewValue as OutputViewModel);
    }

    private void Attach(OutputViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(viewModel, _viewModel))
            return;

        _viewModel = viewModel;
        _viewModel.ScrollToEndRequested += ScrollToEnd;
    }

    private void Detach()
    {
        if (_viewModel is null)
            return;

        _viewModel.ScrollToEndRequested -= ScrollToEnd;
        _viewModel = null;
    }

    private void ScrollToEnd()
    {
        if (LinesList.Items.Count == 0)
            return;

        // ScrollIntoView on a virtualised list is cheap and keeps the newest line pinned.
        var scrollViewer = FindScrollViewer(LinesList);

        scrollViewer?.ScrollToBottom();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;

        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));

            if (found is not null)
                return found;
        }

        return null;
    }
}
