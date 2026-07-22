using System.Windows.Controls;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.Views;

public partial class OutputView : UserControl
{
    private readonly OutputService Output;


    public OutputView(OutputService output)
    {
        InitializeComponent();

        Output = output;


        OutputTextBox.Text =
            Output.GetContent();


        Output.MessageReceived += OnMessageReceived;
    }



    private void OnMessageReceived(string text)
    {
        Dispatcher.Invoke(() =>
        {
            OutputTextBox.AppendText(
                text + "\n");

            OutputTextBox.ScrollToEnd();
        });
    }
}