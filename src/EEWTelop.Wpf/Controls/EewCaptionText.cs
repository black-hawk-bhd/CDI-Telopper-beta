using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using EEWTelop.Application.Display;

namespace EEWTelop.Wpf.Controls;

public sealed class EewCaptionText : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(EewCaptionText),
        new PropertyMetadata(string.Empty, OnCaptionChanged));

    public static readonly DependencyProperty AreaLabelsProperty = DependencyProperty.Register(
        nameof(AreaLabels), typeof(IReadOnlyList<EewAreaLabel>), typeof(EewCaptionText),
        new PropertyMetadata(null, OnCaptionChanged));

    public string SourceText
    {
        get => (string?)GetValue(SourceTextProperty) ?? string.Empty;
        set => SetValue(SourceTextProperty, value);
    }

    public IReadOnlyList<EewAreaLabel>? AreaLabels
    {
        get => (IReadOnlyList<EewAreaLabel>?)GetValue(AreaLabelsProperty);
        set => SetValue(AreaLabelsProperty, value);
    }

    private static void OnCaptionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (EewCaptionText)sender;
        var labels = control.AreaLabels;
        control.Inlines.Clear();
        if (labels is null || !string.Equals(control.SourceText,
                string.Join('　', labels.Select(static label => label.Name)), StringComparison.Ordinal))
        {
            control.Text = control.SourceText;
            return;
        }

        for (int index = 0; index < labels.Count; index++)
        {
            if (index > 0) control.Inlines.Add(new Run("　"));
            var run = new Run(labels[index].Name);
            if (labels[index].IsChanged) run.Foreground = System.Windows.Media.Brushes.Orange;
            control.Inlines.Add(run);
        }
    }
}
