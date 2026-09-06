using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;

namespace EEWTelop.Wpf.Controls;

public sealed class ReviewRubyText : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(ReviewRubyText),
        new PropertyMetadata(string.Empty, OnSourceTextChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    private static void OnSourceTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (ReviewRubyText)sender;
        control.Inlines.Clear();
        AutomationProperties.SetName(control, args.NewValue as string ?? string.Empty);
        foreach (var segment in PlaceNameReadings.Split(args.NewValue as string))
        {
            if (segment.Reading is null)
            {
                control.Inlines.Add(new Run(segment.Text));
                continue;
            }

            var ruby = new StackPanel { Margin = new Thickness(1, 0, 1, 0) };
            ruby.Children.Add(new TextBlock
            {
                Text = segment.Reading,
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            ruby.Children.Add(new TextBlock
            {
                Text = segment.Text,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            control.Inlines.Add(new InlineUIContainer(ruby) { BaselineAlignment = BaselineAlignment.Bottom });
        }
    }
}
