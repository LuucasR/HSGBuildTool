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
