using System.Windows;
using System.Windows.Controls;

namespace EEWTelop.Wpf.Controls;

public sealed class CaptionBadgeLayout : Decorator
{
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(OutlinedText), typeof(CaptionBadgeLayout),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsArrange));

    public OutlinedText? Caption
    {
        get => (OutlinedText?)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is not Border { Child: TextBlock label } badge)
            return base.ArrangeOverride(arrangeSize);

        double height = badge.DesiredSize.Height;
        double availableOffset = Math.Max(0, arrangeSize.Height - height);
        double top = availableOffset / 2;
        if (Caption is { Visibility: Visibility.Visible } caption && caption.DesiredSize.Height > 0)
        {
            // Center the letters using their measured baselines and font sizes.
            double captionCenter = caption.FirstLineBaseline - caption.FontSize / 2;
            double labelCenter = badge.BorderThickness.Top + badge.Padding.Top + label.BaselineOffset - label.FontSize / 2;
            top = Math.Clamp(captionCenter - labelCenter, 0, availableOffset);
        }

        badge.Arrange(new Rect(0, top, arrangeSize.Width, height));
        return arrangeSize;
    }
}
