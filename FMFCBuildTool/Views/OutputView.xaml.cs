using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.Views;

public partial class OutputView : UserControl
{
    private readonly OutputService Output;

    public OutputView(OutputService output)
    {
        InitializeComponent();

        Output = output;

        Output.MessageReceived += OnMessageReceived;

        foreach (var line in Output.GetContent().Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                AppendLine(line);
        }
    }

    private void OnMessageReceived(string text)
    {
        Dispatcher.Invoke(() =>
        {
            if (string.IsNullOrEmpty(text))
            {
                OutputTextBox.Document.Blocks.Clear();
                return;
            }

            AppendLine(text);
        });
    }

    private void AppendLine(string line)
    {
        var paragraph = new Paragraph
        {
            Margin = new System.Windows.Thickness(0)
        };

        paragraph.Inlines.Add(new Run(line)
        {
            Foreground = GetBrush(line)
        });

        OutputTextBox.Document.Blocks.Add(paragraph);
        OutputTextBox.ScrollToEnd();
    }

    private Brush GetBrush(string line)
    {
        var text = line.ToLowerInvariant();

        // Errores
        if (text.Contains("[error]") ||
            text.Contains("fatal error") ||
            text.Contains(" error:") ||
            text.StartsWith("error:") ||
            text.Contains("logcook: error") ||
            text.Contains("packagingresults: error") ||
            text.Contains("automationtool exiting with exitcode") ||
            text.Contains("exception"))
        {
            return Brushes.IndianRed;
        }

        // Warnings
        if (text.Contains("warning"))
        {
            return Brushes.Gold;
        }

        return Brushes.WhiteSmoke;
    }
}