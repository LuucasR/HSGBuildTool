using System.Windows;
using System.Windows.Input;

namespace FMFCBuildTool.Views;

public partial class PresetNameWindow : Window
{
    public PresetNameWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => NameTextBox.Focus();
    }

    public string PresetName { get; private set; } = "";

    /// <summary>
    /// Asks for a preset name, or null if the user backs out. Lives here rather than in a
    /// view-model so that Package and the two commandlet pages, which now all have named
    /// presets, share one dialog instead of three copies of the same six lines.
    /// </summary>
    public static string? Prompt()
    {
        var dialog = new PresetNameWindow
        {
            Owner = Application.Current?.MainWindow
        };

        return dialog.ShowDialog() == true ? dialog.PresetName : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            TryAccept();
    }

    private void TryAccept()
    {
        var name = NameTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = "Enter a name for the preset.";
            ErrorText.Visibility = Visibility.Visible;

            return;
        }

        PresetName = name;

        DialogResult = true;
    }
}
