using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace EEWTelop.Wpf.Controls;

/// <summary>
/// Hosts a native TextBlock for stable Japanese layout and applies a single
/// zero-offset shadow to the same glyph layer. Separate outline text caused
/// different wrapping and left detached digits on tsunami rows.
/// </summary>
public sealed class OutlinedText : Border
{
    private static readonly FrameworkPropertyMetadata LayoutMetadata = new(
        defaultValue: null,
        FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
        OnTextPropertyChanged);

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedText), LayoutMetadata);

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(OutlinedText),
        new FrameworkPropertyMetadata(
            new FontFamily("BIZ UDPGothic, Yu Gothic UI, Meiryo"),
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnTextPropertyChanged));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(OutlinedText),
        new FrameworkPropertyMetadata(
            58d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnTextPropertyChanged));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(OutlinedText),
        new FrameworkPropertyMetadata(
            FontWeights.Black,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnTextPropertyChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(OutlinedText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, OnTextPropertyChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(OutlinedText),
        new FrameworkPropertyMetadata(
            Brushes.Black,
            FrameworkPropertyMetadataOptions.AffectsRender,
            OnTextPropertyChanged));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(OutlinedText),
        new FrameworkPropertyMetadata(
            6d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnStrokeThicknessChanged));

    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(
        nameof(LineHeight), typeof(double), typeof(OutlinedText),
        new FrameworkPropertyMetadata(
            72.5d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnTextPropertyChanged));

    public static readonly DependencyProperty LetterSpacingProperty = DependencyProperty.Register(
        nameof(LetterSpacing), typeof(double), typeof(OutlinedText),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnTextPropertyChanged));

    private readonly TextBlock _textBlock;

    public OutlinedText()
    {
        _textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        Child = _textBlock;
        UpdateChild();
        UpdatePadding();
    }

    public string Text { get => (string?)GetValue(TextProperty) ?? string.Empty; set => SetValue(TextProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
    public double LetterSpacing { get => (double)GetValue(LetterSpacingProperty); set => SetValue(LetterSpacingProperty, value); }

    private static void OnTextPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((OutlinedText)dependencyObject).UpdateChild();

    private static void OnStrokeThicknessChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (OutlinedText)dependencyObject;
        control.UpdatePadding();
        control.UpdateChild();
    }

    private void UpdateChild()
    {
        if (_textBlock is null)
        {
            return;
        }

        _textBlock.Text = Text;
        Visibility = string.IsNullOrEmpty(Text)
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        _textBlock.FontFamily = FontFamily;
        _textBlock.FontSize = FontSize;
        _textBlock.FontWeight = FontWeight;
        _textBlock.Foreground = Fill;
        _textBlock.LineHeight = Math.Max(FontSize, LineHeight);
        _textBlock.LayoutTransform = new ScaleTransform(GetHorizontalScale(), 1);
        Color shadowColor = Stroke is SolidColorBrush brush ? brush.Color : Colors.Black;
        _textBlock.Effect = new DropShadowEffect
        {
            Color = shadowColor,
            BlurRadius = Math.Max(2, StrokeThickness * 0.7),
            ShadowDepth = 0,
            Opacity = 1,
            RenderingBias = RenderingBias.Quality,
        };
    }

    private void UpdatePadding()
    {
        double inset = Math.Max(0, StrokeThickness);
        Padding = new Thickness(inset);
    }

    private double GetHorizontalScale() => FontSize <= 0
        ? 1
        : Math.Clamp(1 + LetterSpacing / FontSize, 0.5, 1.5);
}
