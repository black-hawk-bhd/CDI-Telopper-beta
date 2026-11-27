using System.Windows;
using System.Windows.Controls;

namespace EEWTelop.Wpf.Controls;

public sealed class TsunamiCaptionPanel : WrapPanel
{
    protected override Size MeasureOverride(Size constraint)
    {
        Size size = base.MeasureOverride(constraint);
        if (GetAlignedSlots(constraint.Width) is { } slots)
            size.Height = Math.Max(slots.Area.Bottom, slots.Note.Bottom);

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (GetAlignedSlots(finalSize.Width) is not { } slots)
            return base.ArrangeOverride(finalSize);

        InternalChildren[0].Arrange(slots.Area);
        InternalChildren[1].Arrange(slots.Note);
        return finalSize;
    }

    private (Rect Area, Rect Note)? GetAlignedSlots(double width)
    {
        if (InternalChildren.Count != 2 ||
            InternalChildren[0] is not OutlinedText { Visibility: Visibility.Visible } area ||
            InternalChildren[1] is not OutlinedText { Visibility: Visibility.Visible } note ||
            area.DesiredSize.Width + note.DesiredSize.Width > width)
        {
            return null;
        }

        double offset = area.FirstLineBaseline - note.FirstLineBaseline;
        return (
            new Rect(new Point(0, Math.Max(0, -offset)), area.DesiredSize),
            new Rect(new Point(area.DesiredSize.Width, Math.Max(0, offset)), note.DesiredSize));
    }
}
