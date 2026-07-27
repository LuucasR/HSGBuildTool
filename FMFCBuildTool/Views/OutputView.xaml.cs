using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using FMFCBuildTool.Services;

namespace FMFCBuildTool.Views;

public partial class OutputView : UserControl
{
    private readonly OutputService Output;
    private readonly List<string> Lines = [];

    private OutputFilter CurrentFilter = OutputFilter.Log;

    private enum OutputFilter
    {
        Log,
        Warning,
        Error
    }

    public OutputView(OutputService output)
    {
        InitializeComponent();

        Output = output;

        Output.MessageReceived += OnMessageReceived;

        foreach (var line in Output.GetContent().Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                Lines.Add(line);
        }
        FilterComboBox.SelectedIndex = 0;
        RefreshView();
    }

    private void OnMessageReceived(string text)
    {
        Dispatcher.Invoke(() =>
        {
            if (string.IsNullOrEmpty(text))
            {
                Lines.Clear();
                OutputTextBox.Document.Blocks.Clear();
                return;
            }

            Lines.Add(text);

            if (PassFilter(text))
                AppendLine(text);
        });
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CurrentFilter = (OutputFilter)FilterComboBox.SelectedIndex;
        RefreshView();
    }

    private void RefreshView()
    {

        OutputTextBox.Document.Blocks.Clear();

        foreach (var line in Lines)
        {
            if (PassFilter(line))
                AppendLine(line);
        }

        OutputTextBox.ScrollToEnd();
    }

    private bool PassFilter(string line)
    {
        return CurrentFilter switch
        {
            OutputFilter.Log => true,
            OutputFilter.Warning => IsWarning(line),
            OutputFilter.Error => IsError(line),
            _ => true
        };
    }

    private void AppendLine(string line)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0)
        };

        paragraph.Inlines.Add(new Run(line)
        {
            Foreground = GetBrush(line)
        });

        OutputTextBox.Document.Blocks.Add(paragraph);
    }

    private Brush GetBrush(string line)
    {
        if (IsError(line))
            return Brushes.IndianRed;

        if (IsWarning(line))
            return Brushes.Gold;

        return Brushes.WhiteSmoke;
    }

    private bool IsWarning(string line)
    {
        return line.ToLowerInvariant().Contains("warning");
    }

    private bool IsError(string line)
    {
        var text = line.ToLowerInvariant();

        return text.Contains("[error]") ||
               text.Contains("fatal error") ||
               text.Contains(" error:") ||
               text.StartsWith("error:") ||
               text.Contains("logcook: error") ||
               text.Contains("packagingresults: error") ||
               text.Contains("automationtool exiting with exitcode") ||
               text.Contains("exception");
    }
}