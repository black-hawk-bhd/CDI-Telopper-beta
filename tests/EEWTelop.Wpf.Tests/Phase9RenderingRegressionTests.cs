using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EEWTelop.Application.Configuration;
using EEWTelop.Application.Coordination;
using EEWTelop.Application.Display;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Controls;
using EEWTelop.Wpf.ViewModels;
using EEWTelop.Wpf.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class Phase9RenderingRegressionTests
{
    private static readonly double[] FontScales = [0.8, 1.0, 1.2];

    private static readonly DateTimeOffset Now =
        new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void RequiredScenariosRenderAtFullHdWithoutVisibleElementOverflow()
    {
        RunSta(() =>
        {
            AppSettings settings = AppSettings.CreateDefault();
            var composer = new PageComposer();
            Dictionary<string, DisplayProgram> programs = TestScenarioCatalog.Create(Now)
                .ToDictionary(
                    static scenario => scenario.Id,
                    scenario => composer.Compose(scenario.Event, settings.Display));
            DisplayProgram productionEew = programs["eew-warning"] with
            {
                SourceMode = SourceMode.Production,
                RehearsalLabel = string.Empty,
            };
            var cases = new List<(DisplayProgram Program, int Page, DisplaySettings Settings)>
            {
                (productionEew, 0, settings.Display),
                (programs["eew-warning"], 0, settings.Display),
                (programs["eew-cancel"], 0, settings.Display),
            };
            cases.AddRange(Enumerable.Range(0, programs["scale-prompt"].Pages.Count)
                .Select(index => (programs["scale-prompt"], index, settings.Display)));
            cases.AddRange(Enumerable.Range(0, programs["detail-scale"].Pages.Count)
                .Select(index => (programs["detail-scale"], index, settings.Display)));
            cases.AddRange(Enumerable.Range(0, programs["tsunami-13"].Pages.Count)
                .Select(index => (programs["tsunami-13"], index, settings.Display)));
            cases.AddRange(Enumerable.Range(0, programs["foreign"].Pages.Count)
                .Select(index => (programs["foreign"], index, settings.Display)));
            foreach (double scale in FontScales)
            {
                cases.Add((programs["detail-scale"], 0, settings.Display with { FontScale = scale }));
            }

            cases.Add((programs["detail-scale"], 0, settings.Display with
            {
                LetterSpacingEm = 0.2,
                LineSpacing = 1.5,
                ShowPageIndicator = false,
            }));

            foreach ((DisplayProgram program, int page, DisplaySettings display) in cases)
            {
                (TelopView view, OverlayViewModel overlay, byte[] pixels) = Render(
                    program,
                    page,
                    display);
                Assert.IsTrue(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0));
                Assert.AreEqual(program.Pages[page].AccessibleText, overlay.AccessibleText);
                AssertVisibleElementsInsideCanvas(view);
                if (!display.ShowPageIndicator)
                {
                    Assert.AreEqual(string.Empty, overlay.PageIndicator);
                    Assert.IsFalse(overlay.HasPageIndicator);
                    var pageIndicatorBorder = (Border?)view.FindName("PageIndicatorBorder");
                    Assert.IsNotNull(pageIndicatorBorder);
                    Assert.AreEqual(Visibility.Collapsed, pageIndicatorBorder.Visibility);
                }
            }
        });
    }

    [TestMethod]
    public void TransparentGreenAndBlueBackgroundPixelsMatchConfiguredMode()
    {
        RunSta(() =>
        {
            DisplayProgram program = new PageComposer().Compose(
                TestScenarioCatalog.Create(Now).Single(item => item.Id == "eew-warning").Event,
                AppSettings.CreateDefault().Display);
            DisplaySettings baseline = AppSettings.CreateDefault().Display;

            byte[] transparent = Render(program, 0, baseline with
            {
                BackgroundMode = BackgroundMode.Transparent,
            }).Pixels;
            byte[] green = Render(program, 0, baseline with
            {
                BackgroundMode = BackgroundMode.Green,
            }).Pixels;
            byte[] blue = Render(program, 0, baseline with
            {
                BackgroundMode = BackgroundMode.Blue,
            }).Pixels;

            AssertPixel(transparent, blue: 0, green: 0, red: 0, alpha: 0);
            AssertPixel(green, blue: 0, green: 255, red: 0, alpha: 255);
            AssertPixel(blue, blue: 255, green: 0, red: 0, alpha: 255);
        });
    }

    [TestMethod]
    public void EewTrainingContentMatchesObsOriginAndDoesNotOverlapTrainingBanner()
    {
        RunSta(() =>
        {
            DisplaySettings settings = AppSettings.CreateDefault().Display;
            DisplayProgram program = new PageComposer().Compose(
                TestScenarioCatalog.Create(Now).Single(item => item.Id == "eew-warning").Event,
                settings);
            (TelopView view, OverlayViewModel overlay, _) = Render(program, 0, settings);

            OverlayBlockViewModel headerBlock = overlay.Blocks.Single(block =>
                block.StyleToken == DisplayStyleTokens.EewHeader);
            Border header = FindVisualChildren<Border>(view)
                .Single(border => ReferenceEquals(border.DataContext, headerBlock));
            Point headerOrigin = header.TransformToAncestor(view).Transform(new Point());
            Assert.AreEqual(8, headerOrigin.X, 1, "EEWの左端が旧HTML版の全幅表示と一致しません。");
            Assert.AreEqual(140, headerOrigin.Y, 1, "EEWの上端がOBS表示領域と一致しません。");
            Assert.AreEqual(1904, header.ActualWidth, 1, "EEW見出しが全幅になっていません。");
            var headerBrush = Assert.IsInstanceOfType<SolidColorBrush>(header.Background);
            Assert.AreEqual(Color.FromRgb(0xA8, 0x32, 0x18), headerBrush.Color);

            var eewPanel = (Grid?)view.FindName("EewPanel");
            Assert.IsNotNull(eewPanel);
            var panelBrush = Assert.IsInstanceOfType<SolidColorBrush>(eewPanel.Background);
            Assert.AreEqual(Color.FromRgb(0x3A, 0x3A, 0xD0), panelBrush.Color);

            OverlayBlockViewModel warningBlock = overlay.Blocks.Single(block =>
                block.StyleToken == DisplayStyleTokens.EewWarning);
            TextBlock warningText = FindVisualChildren<TextBlock>(view)
                .Single(text => ReferenceEquals(text.DataContext, warningBlock) &&
                    text.Text == warningBlock.PrimaryText);
            var warningBrush = Assert.IsInstanceOfType<SolidColorBrush>(warningText.Foreground);
            Assert.AreEqual(Color.FromRgb(0xF0, 0xF0, 0x00), warningBrush.Color);

            TextBlock bannerText = FindVisualChildren<TextBlock>(view)
                .Single(text => text.Text == overlay.RehearsalLabel);
            double bannerBottom = bannerText.TransformToAncestor(view)
                .Transform(new Point(0, bannerText.ActualHeight)).Y;
            Assert.IsTrue(headerOrigin.Y > bannerBottom,
                "EEWの訓練表示が黄色の識別バナーと重なっています。");
        });
    }

    [TestMethod]
    public void SubtitleRenderingMatchesHtmlGeometryAndTypography()
    {
        RunSta(() =>
        {
            DisplaySettings settings = AppSettings.CreateDefault().Display with
            {
                FontScale = 1.2,
                LetterSpacingEm = 0.05,
                LineSpacing = 1.1,
            };
            DisplayProgram source = new PageComposer().Compose(
                TestScenarioCatalog.Create(Now).Single(item => item.Id == "detail-scale").Event,
                settings);
            DisplayProgram program = source with
            {
                SourceMode = SourceMode.Sandbox,
                RehearsalLabel = "サンドボックス／訓練",
            };
            int pageIndex = Enumerable.Range(0, program.Pages.Count)
                .First(index => program.Pages[index].Blocks.Any(block =>
                    block.StyleToken == DisplayStyleTokens.Intensity &&
                    string.IsNullOrWhiteSpace(block.Badge)));

            (TelopView view, OverlayViewModel overlay, _) = Render(program, pageIndex, settings);

            Assert.AreEqual("サンドボックス／訓練", overlay.RehearsalLabel);
            Assert.AreEqual(58 * settings.FontScale, overlay.SubtitleFontSize, 0.001);
            Assert.AreEqual(58 * settings.FontScale * 1.25 * settings.LineSpacing, overlay.SubtitleLineHeight, 0.001);
            Assert.AreEqual(58 * settings.FontScale * (0.02 + settings.LetterSpacingEm), overlay.SubtitleLetterSpacing, 0.001);
            Assert.AreEqual("#FF3B8FD4", new OverlayBlockViewModel(
                "震度1", "震度1", true, "地域", "", DisplayStyleTokens.Intensity).BadgeBackground);
            var longPeriodClass1 = new OverlayBlockViewModel(
                "長周期階級1", "長周期階級1", true, "地域", "", DisplayStyleTokens.Intensity);
            var longPeriodClass2 = new OverlayBlockViewModel(
                "長周期階級2", "長周期階級2", true, "地域", "", DisplayStyleTokens.Intensity);
            var longPeriodClass3 = new OverlayBlockViewModel(
                "長周期階級3", "長周期階級3", true, "地域", "", DisplayStyleTokens.Intensity);
            var longPeriodClass4 = new OverlayBlockViewModel(
                "長周期階級4", "長周期階級4", true, "地域", "", DisplayStyleTokens.Intensity);
            Assert.AreEqual("#FF075CFF", longPeriodClass1.BadgeBackground);
            Assert.AreEqual("#FFFFFFFF", longPeriodClass1.BadgeForeground);
            Assert.AreEqual("#FFFFE600", longPeriodClass2.BadgeBackground);
            Assert.AreEqual("#FF222222", longPeriodClass2.BadgeForeground);
            Assert.AreEqual("#FFF04416", longPeriodClass3.BadgeBackground);
            Assert.AreEqual("#FFFFFFFF", longPeriodClass3.BadgeForeground);
            Assert.AreEqual("#FFA00032", longPeriodClass4.BadgeBackground);
            Assert.AreEqual("#FFFFFFFF", longPeriodClass4.BadgeForeground);
            var levelFive = new OverlayBlockViewModel(
                "レベル５大雨特別警報",
                "レベル５大雨特別警報",
                true,
                "地域",
                "",
                DisplayStyleTokens.WeatherSpecialWarning);
            Assert.AreEqual("#FF08050A", levelFive.BadgeBackground);
            Assert.AreEqual("#FFFFFFFF", levelFive.BadgeForeground);
            Assert.AreEqual("#FFD9D9D9", levelFive.BadgeBorderBrush);
            Assert.AreEqual(2, levelFive.BadgeBorderThickness);

            var levelFour = new OverlayBlockViewModel(
                "レベル４土砂災害危険警報",
                "レベル４土砂災害危険警報",
                true,
                "地域",
                "",
                DisplayStyleTokens.WeatherDangerWarning);
            Assert.AreEqual("#FF8F1AA6", levelFour.BadgeBackground);
            Assert.AreEqual("#FFFFFFFF", levelFour.BadgeForeground);

            int continuationIndex = overlay.Blocks.ToList().FindIndex(block =>
                block.StyleToken == DisplayStyleTokens.Intensity &&
                !block.IsBadgeVisible &&
                block.HasBadgeColumn);
            Assert.IsTrue(continuationIndex > 0);
            OverlayBlockViewModel preceding = overlay.Blocks[continuationIndex - 1];
            OverlayBlockViewModel continuation = overlay.Blocks[continuationIndex];
            Assert.AreEqual(preceding.BadgePlaceholder, continuation.BadgePlaceholder);
            Assert.AreEqual(0, continuation.BadgeOpacity);

            OutlinedText precedingText = FindVisualChildren<OutlinedText>(view)
                .First(item => ReferenceEquals(item.DataContext, preceding) && item.Text == preceding.PrimaryText);
            OutlinedText continuationText = FindVisualChildren<OutlinedText>(view)
                .First(item => ReferenceEquals(item.DataContext, continuation) && item.Text == continuation.PrimaryText);
            double precedingLeft = precedingText.TransformToAncestor(view).Transform(new Point()).X;
            double continuationLeft = continuationText.TransformToAncestor(view).Transform(new Point()).X;
            Assert.AreEqual(precedingLeft, continuationLeft, 1, "継続行の字幕開始位置が先頭行と一致しません。");

            Assert.IsFalse(FindVisualChildren<Border>(view).Any(border =>
                border.IsVisible &&
                border.DataContext is OverlayBlockViewModel &&
                border.ActualWidth > 1000 &&
                border.Background is SolidColorBrush brush &&
                brush.Color.A > 0), "字幕行全体を覆う背景色が残っています。");

            TextBlock page = FindVisualChildren<TextBlock>(view)
                .Single(item => item.Text == overlay.PageIndicator);
            double pageTop = page.TransformToAncestor(view).Transform(new Point()).Y;
            double lastSubtitleBottom = FindVisualChildren<OutlinedText>(view)
                .Where(item => item.DataContext is OverlayBlockViewModel)
                .Max(item => item.TransformToAncestor(view).Transform(new Point(0, item.ActualHeight)).Y);
            Assert.IsTrue(pageTop >= lastSubtitleBottom - 1 && pageTop <= lastSubtitleBottom + 60,
                "ページ番号が字幕行の直下に配置されていません。");
        });
    }

    [TestMethod]
    public void TsunamiRowsShareOneForecastAreaColumnWithoutDetachedShadowText()
    {
        RunSta(() =>
        {
            DisplaySettings settings = AppSettings.CreateDefault().Display with
            {
                ShowTsunamiForecast = true,
            };
            DisplayProgram program = new PageComposer().Compose(
                TestScenarioCatalog.Create(Now).Single(item => item.Id == "tsunami-13").Event,
                settings);
            int threeRowPageIndex = program.Pages
                .Select((page, index) => new
                {
                    Index = index,
                    RowCount = page.Blocks.Count(block =>
                        block.StyleToken == DisplayStyleTokens.Tsunami),
                })
                .First(item => item.RowCount == 3)
                .Index;
            (TelopView view, OverlayViewModel overlay, _) = Render(
                program,
                threeRowPageIndex,
                settings);

            OutlinedText[] areaText = FindVisualChildren<OutlinedText>(view)
                .Where(item => VisualTreeHelper.GetParent(item) is WrapPanel &&
                    item.DataContext is OverlayBlockViewModel block &&
                    block.IsTsunami &&
                    item.Text == block.PrimaryText)
                .ToArray();
            Assert.HasCount(3, areaText);
            double[] leftEdges = areaText
                .Select(item => item.TransformToAncestor(view).Transform(new Point()).X)
                .ToArray();
            Assert.IsTrue(leftEdges.Max() - leftEdges.Min() <= 1,
                "津波予報区の開始位置が揃っていません。");

            foreach (OutlinedText text in areaText)
            {
                Assert.AreEqual(1, FindVisualChildren<TextBlock>(text)
                    .Count(child => child.Text == text.Text),
                    "字幕と影が別の文字レイヤーとして重複しています。");
            }
        });
    }

    private static (TelopView View, OverlayViewModel Overlay, byte[] Pixels) Render(
        DisplayProgram program,
        int pageIndex,
        DisplaySettings settings)
    {
        var overlay = new OverlayViewModel();
        overlay.Apply(new CoordinatorSnapshot(
            program,
            program.Pages[pageIndex],
            pageIndex,
            TimeSpan.Zero,
            Now,
            null,
            null,
            [],
            null,
            new CoordinatorDecision(CoordinatorDecisionKind.Evaluated, "render"),
            false), settings);
        var view = new TelopView
        {
            Width = 1920,
            Height = 1080,
            DataContext = overlay,
        };
        view.Dispatcher.Invoke(static () => { }, DispatcherPriority.DataBind);
        view.Measure(new Size(1920, 1080));
        view.Arrange(new Rect(0, 0, 1920, 1080));
        view.UpdateLayout();
        view.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        string? renderDirectory = Environment.GetEnvironmentVariable("EEWTELOP_RENDER_OUTPUT");
        if (!string.IsNullOrWhiteSpace(renderDirectory) && program.Kind == EventKind.Quake)
        {
            Directory.CreateDirectory(renderDirectory);
            string safeProgramId = string.Concat(program.ProgramId.Select(character =>
                Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
            string variant = FormattableString.Invariant(
                $"scale-{settings.FontScale:0.00}-letter-{settings.LetterSpacingEm:0.00}-line-{settings.LineSpacing:0.00}")
                .Replace('.', '_');
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(Path.Combine(
                renderDirectory,
                $"{safeProgramId}-page-{pageIndex + 1}-{variant}-{program.SourceMode}.png"));
            encoder.Save(stream);
        }
        var pixels = new byte[1920 * 1080 * 4];
        bitmap.CopyPixels(pixels, 1920 * 4, 0);
        return (view, overlay, pixels);
    }

    private static void AssertVisibleElementsInsideCanvas(DependencyObject root)
    {
        foreach (TextBlock textBlock in FindVisualChildren<TextBlock>(root)
            .Where(static item => item.IsVisible && !string.IsNullOrWhiteSpace(item.Text)))
        {
            Rect bounds = textBlock.TransformToAncestor((Visual)root).TransformBounds(
                new Rect(textBlock.RenderSize));
            Assert.IsTrue(bounds.Left >= -1, $"Text starts outside canvas: {textBlock.Text}");
            Assert.IsTrue(bounds.Top >= -1, $"Text starts above canvas: {textBlock.Text}");
            Assert.IsTrue(bounds.Right <= 1921, $"Text ends outside canvas: {textBlock.Text}");
            Assert.IsTrue(bounds.Bottom <= 1081, $"Text ends below canvas: {textBlock.Text}");
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void AssertPixel(
        byte[] pixels,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        Assert.AreEqual(blue, pixels[0]);
        Assert.AreEqual(green, pixels[1]);
        Assert.AreEqual(red, pixels[2]);
        Assert.AreEqual(alpha, pixels[3]);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
