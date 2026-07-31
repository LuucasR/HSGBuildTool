using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FMFCBuildTool.Views;

/// <summary>
/// Attached properties that render <see cref="TextProperty"/> into a TextBlock with
/// every occurrence of <see cref="TermProperty"/> highlighted.
/// </summary>
/// <remarks>
/// Used by the log viewer so a search shows <em>where</em> the match is inside a long
/// line, instead of just filtering the line in.
/// </remarks>
public static class HighlightBehavior
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(HighlightBehavior),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TermProperty =
        DependencyProperty.RegisterAttached(
            "Term",
            typeof(string),
            typeof(HighlightBehavior),
            new PropertyMetadata(null, OnChanged));

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetTerm(DependencyObject element, string value) => element.SetValue(TermProperty, value);

    public static string GetTerm(DependencyObject element) => (string)element.GetValue(TermProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock)
            return;

        var text = GetText(textBlock) ?? "";
        var term = GetTerm(textBlock) ?? "";

        textBlock.Inlines.Clear();

        if (term.Length == 0 || text.Length == 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        var highlight = Application.Current.TryFindResource("LogHighlight") as Brush ?? Brushes.Goldenrod;

        var index = 0;

        while (index < text.Length)
        {
            var match = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase);

            if (match < 0)
            {
                textBlock.Inlines.Add(new Run(text[index..]));
                break;
            }

            if (match > index)
                textBlock.Inlines.Add(new Run(text[index..match]));

            textBlock.Inlines.Add(new Run(text.Substring(match, term.Length))
            {
                Background = highlight,
                FontWeight = FontWeights.SemiBold
            });

            index = match + term.Length;
        }
    }
}
