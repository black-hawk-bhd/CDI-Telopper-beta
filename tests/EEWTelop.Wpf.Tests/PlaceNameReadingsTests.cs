using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EEWTelop.Wpf.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class PlaceNameReadingsTests
{
    private static readonly string[] ScreenshotReadings =
    [
        "ちばけん", "おおあみしらさとし", "いちはらし", "とうがねし", "たてやまし",
        "いすみし", "いちのみやまち", "かつうらし", "みなみぼうそうし", "おおたきまち",
        "おんじゅくまち", "むつざわまち", "もばらし", "ちょうなんまち", "かもがわし",
    ];

    [TestMethod]
    public void ScreenshotMunicipalitiesHaveVerifiedReadings()
    {
        const string text = "千葉県 大網白里市 市原市 東金市 館山市 いすみ市 一宮町 勝浦市 南房総市 大多喜町 御宿町 睦沢町 茂原市 長南町 鴨川市";
        var segments = PlaceNameReadings.Split(text);
        Assert.AreEqual(text, string.Concat(segments.Select(segment => segment.Text)));
        CollectionAssert.AreEqual(ScreenshotReadings,
            segments.Where(segment => segment.Reading is not null).Select(segment => segment.Reading).ToArray());
    }

    [TestMethod]
    public void PrefectureResolvesDifferentReadingsAndUnknownNamesRemainPlain()
    {
        Assert.IsNull(PlaceNameReadings.Split("朝日町").Single().Reading);
        Assert.AreEqual("あさひまち", PlaceNameReadings.Split("山形県 朝日町")[^1].Reading);
        Assert.AreEqual("あさひちょう", PlaceNameReadings.Split("山形県 朝日町。三重県 朝日町")[^1].Reading);
        const string text = "架空の地名\r\n新たに発表。M不明 <test>";
        Assert.AreEqual(text, string.Concat(PlaceNameReadings.Split(text).Select(segment => segment.Text)));
        Assert.IsTrue(PlaceNameReadings.Split(text).All(segment => segment.Reading is null));
        Assert.AreEqual(0, PlaceNameReadings.Split(null).Count);
    }

    [TestMethod]
    public void LongestNameWinsWithoutDecoratingItsSuffixAgain()
    {
        var segments = PlaceNameReadings.Split("千葉県 長生郡睦沢町");
        Assert.AreEqual("長生郡睦沢町", segments[^1].Text);
        Assert.AreEqual("ちょうせいぐんむつざわまち", segments[^1].Reading);
    }

    [TestMethod]
    public void ReviewControlWrapsRubyAndClearsItWhenSelectionChanges()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var control = new ReviewRubyText
                {
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap,
                    SourceText = "千葉県 大網白里市 市原市 東金市 館山市 新たに発表。",
                };
                control.Measure(new Size(240, double.PositiveInfinity));
                control.Arrange(new Rect(0, 0, 240, control.DesiredSize.Height));
                Assert.IsTrue(control.ActualHeight > 40);
                Assert.IsTrue(control.DesiredSize.Width <= 240);
                Assert.AreEqual(5, control.Inlines.OfType<InlineUIContainer>().Count());
                foreach (var inline in control.Inlines.OfType<InlineUIContainer>())
                {
                    var panel = (StackPanel)inline.Child;
                    Assert.AreEqual(2, panel.Children.Count);
                    Assert.AreEqual(16d, ((TextBlock)panel.Children[1]).FontSize);
                    Assert.IsTrue(panel.ActualHeight >= 26);
                }

                string? renderDirectory = Environment.GetEnvironmentVariable("EEWTELOP_RENDER_OUTPUT");
                if (!string.IsNullOrWhiteSpace(renderDirectory))
                {
                    control.Foreground = Brushes.White;
                    control.FontFamily = new FontFamily("Yu Gothic UI");
                    var border = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(11, 16, 22)),
                        Padding = new Thickness(16),
                        Child = control,
                    };
                    border.Measure(new Size(420, 180));
                    border.Arrange(new Rect(0, 0, 420, 180));
                    border.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(420, 180, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(border);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(renderDirectory);
                    using var stream = File.Create(Path.Combine(renderDirectory, "telegram-review-ruby.png"));
                    encoder.Save(stream);
                }

                control.SourceText = "未登録地名";
                Assert.AreEqual(0, control.Inlines.OfType<InlineUIContainer>().Count());
                Assert.AreEqual("未登録地名", control.Text);
                control.SourceText = string.Empty;
                Assert.AreEqual(0, control.Inlines.Count);
            }
            catch (Exception exception)
            {
                error = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
