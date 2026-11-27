using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    [TestMethod]
    public void WeatherAreaContextIsBesideBadgeWithoutChangingSourceText()
    {
        var block = new OverlayBlockViewModel("大雨警報", "大雨警報", true,
            "福島県　｜継続中\nいわき市　三島町　三春町", "", DisplayStyleTokens.WeatherWarning);
        Assert.IsTrue(block.HasWeatherContext);
        Assert.AreEqual("福島県　｜継続中", block.WeatherContext);
        Assert.AreEqual("いわき市　三島町　三春町", block.SubtitlePrimaryText);
        Assert.IsTrue(block.PrimaryText.Contains("福島県", StringComparison.Ordinal));
        var narrative = block with { PrimaryText = "河川では水位が上昇しています\n注意してください" };
        Assert.IsFalse(narrative.HasWeatherContext);
        Assert.AreEqual(narrative.PrimaryText, narrative.SubtitlePrimaryText);
        Assert.IsFalse((block with { Badge = "" }).HasWeatherContext);
        Assert.IsFalse((block with { StyleToken = DisplayStyleTokens.Summary }).HasWeatherContext);
        var bulletin = block with { Badge = "気象防災速報", WeatherHeading = "宮崎県南部平野部", PrimaryText = "今後3時間以内に線状降水帯が発生する可能性があります" };
        Assert.AreEqual("宮崎県南部平野部", bulletin.WeatherContext);
        Assert.AreEqual("今後3時間以内に線状降水帯が発生する可能性があります", bulletin.SubtitlePrimaryText);
        var explicitHeading = block with { Badge = "竜巻注意情報", WeatherHeading = "栃木県｜発表", PrimaryText = "空の様子に注意してください\n続きの本文" };
        Assert.IsTrue(explicitHeading.HasWeatherContext);
        Assert.AreEqual("栃木県｜発表", explicitHeading.WeatherContext);
        Assert.AreEqual(explicitHeading.PrimaryText, explicitHeading.SubtitlePrimaryText);
    }

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
                Assert.IsNull(view.FindName("PageIndicatorBorder"));
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
    public void EewTrainingContentMatchesObsLayoutAndDoesNotOverlapTrainingBanner()
    {
        RunSta(() =>
        {
            DisplaySettings settings = AppSettings.CreateDefault().Display with { ShowEewEstimates = true };
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
            Assert.AreEqual(119.4, header.ActualHeight, 1, "緊急地震速報の見出しの高さがブラウザー表示と一致しません。");
            var headerBrush = Assert.IsInstanceOfType<SolidColorBrush>(header.Background);
            Assert.AreEqual(Color.FromRgb(0xA8, 0x32, 0x18), headerBrush.Color);
            AssertEewPrimaryLine(FindVisualChildren<EewCaptionText>(header).Single(), view, 156);

            var eewPanel = (Grid?)view.FindName("EewPanel");
            Assert.IsNotNull(eewPanel);
            var panelBrush = Assert.IsInstanceOfType<SolidColorBrush>(eewPanel.Background);
            Assert.AreEqual(Color.FromRgb(0x3A, 0x3A, 0xD0), panelBrush.Color);
            Assert.AreEqual(570, eewPanel.ActualHeight, 1);

            OverlayBlockViewModel warningBlock = overlay.Blocks.Single(block =>
                block.StyleToken == DisplayStyleTokens.EewWarning);
            TextBlock warningText = FindVisualChildren<TextBlock>(view)
                .Single(text => ReferenceEquals(text.DataContext, warningBlock) &&
                    text.Text == warningBlock.PrimaryText);
            var warningBrush = Assert.IsInstanceOfType<SolidColorBrush>(warningText.Foreground);
            Assert.AreEqual(Color.FromRgb(0xF0, 0xF0, 0x00), warningBrush.Color);
            AssertEewPrimaryLine(warningText, view, 265.4);

            OverlayBlockViewModel areaBlock = overlay.Blocks.Single(block => block.StyleToken == DisplayStyleTokens.EewAreas);
            TextBlock[] areaTexts = FindVisualChildren<TextBlock>(view)
                .Where(text => ReferenceEquals(text.DataContext, areaBlock)).ToArray();
            TextBlock regions = areaTexts.Single(text => text.Text == areaBlock.PrimaryText);
            Assert.IsNotNull(areaBlock.EewEstimateText);
            TextBlock estimates = areaTexts.Single(text => text.Text == areaBlock.EewEstimateText);
            AssertEewPrimaryLine(regions, view, 349.9);
            Assert.AreEqual(428.4, estimates.TranslatePoint(new Point(), view).Y, 2);

            TextBlock bannerText = FindVisualChildren<TextBlock>(view)
                .Single(text => text.Text == overlay.RehearsalLabel);
            double bannerBottom = bannerText.TransformToAncestor(view)
                .Transform(new Point(0, bannerText.ActualHeight)).Y;
            Assert.IsTrue(headerOrigin.Y > bannerBottom,
                "EEWの訓練表示が黄色の識別バナーと重なっています。");
        });
    }

    [TestMethod]
    public void ConcurrentEewWarningAndCancellationUseCompactObsRows()
    {
        RunSta(() =>
        {
            var scenario = TestScenarioCatalog.Create(Now).Single(item => item.Id == "eew-concurrent-one-cancel");
            DisplaySettings settings = AppSettings.CreateDefault().Display;
            var composer = new PageComposer();
            var concurrent = new ConcurrentEewProgramComposer();
            DisplayProgram? program = null;
            foreach (TestScenarioStep step in scenario.Steps)
            {
                program = concurrent.Compose((EewEvent)step.Event, composer.Compose(step.Event, settings), settings);
            }

            Assert.IsNotNull(program);
            (TelopView view, OverlayViewModel overlay, _) = Render(program, 0, settings);

            Assert.IsTrue(overlay.IsConcurrentEewProgram);
            Border[] headers = FindVisualChildren<Border>(view)
                .Where(border => border.DataContext is OverlayBlockViewModel { IsEewHeader: true }).ToArray();
            Assert.HasCount(2, headers);
            Assert.IsTrue(headers.Any(header => header.DataContext is OverlayBlockViewModel
                { StyleToken: DisplayStyleTokens.EewHeaderCancel }));
            foreach (Border header in headers)
            {
                Assert.AreEqual(71, header.ActualHeight, 1);
                AssertEewPrimaryLine(FindVisualChildren<EewCaptionText>(header).Single(), view,
                    header.TranslatePoint(new Point(), view).Y + 8);
            }
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
        });
    }

    [TestMethod]
    [DataRow(EventKind.Quake, false, "震度5弱以上 未入電", "震度3")]
    [DataRow(EventKind.Quake, true, "震度5弱", "")]
    [DataRow(EventKind.Tsunami, true, "津波注意報", "")]
    public void EarthquakeAndTsunamiLocationsStartInTheSameColumn(
        EventKind kind, bool hideBadges, string firstBadge, string secondBadge)
    {
        RunSta(() =>
        {
            const string firstLocation = "石川県輪島市";
            const string secondLocation = "石川県珠洲市";
            string style = kind == EventKind.Quake ? DisplayStyleTokens.Intensity : DisplayStyleTokens.Tsunami;
            string note = kind == EventKind.Tsunami ? "〔1m〕" : string.Empty;
            var page = new DisplayPage(0,
                [new(firstBadge, firstLocation, note, style), new(secondBadge, secondLocation, note, style)],
                $"{firstBadge} {firstLocation} {secondBadge} {secondLocation}", null);
            var program = new DisplayProgram("caption-columns", EventId.Create("caption-columns"), kind,
                SourceMode.Production, Now, OverlayPriority.Quake, [page], Now, EndPolicy.AutoHide, string.Empty);
            DisplaySettings settings = AppSettings.CreateDefault().Display with { HideNormalInformationBadges = hideBadges };

            (TelopView view, OverlayViewModel overlay, _) = Render(program, 0, settings);

            Assert.IsTrue(overlay.Blocks.All(block => block.SharesCaptionColumn));
            Assert.AreEqual(hideBadges ? firstBadge : string.Empty, overlay.Blocks[0].SubtitleLabel);
            OutlinedText[] locations = FindVisualChildren<OutlinedText>(view)
                .Where(text => text.ActualHeight > 0 && text.Text is firstLocation or secondLocation).ToArray();
            Assert.HasCount(2, locations);
            double left = locations[0].TransformToAncestor(view).Transform(new Point()).X;
            double nextLeft = locations[1].TransformToAncestor(view).Transform(new Point()).X;
            Assert.AreEqual(left, nextLeft, 1, "地名の開始位置を揃えます。");
        });
    }

    [TestMethod]
    [DataRow(EventKind.Quake, DisplayStyleTokens.Intensity, "震度6弱")]
    [DataRow(EventKind.Tsunami, DisplayStyleTokens.Tsunami, "津波警報")]
    [DataRow(EventKind.Volcano, DisplayStyleTokens.VolcanoWarning, "噴火警報")]
    [DataRow(EventKind.WeatherWarning, DisplayStyleTokens.WeatherWarning, "大雨警報")]
    public void DesktopBadgeIsCenteredOnTheFirstCaptionLine(EventKind kind, string style, string badge)
    {
        RunSta(() =>
        {
            const string areas = "神奈川県横浜市　神奈川県川崎市　神奈川県相模原市　神奈川県横須賀市　神奈川県藤沢市　神奈川県平塚市";
            DisplayBlock block = kind == EventKind.WeatherWarning
                ? new DisplayBlock(badge, "周辺の状況に注意してください", string.Empty, style) { WeatherHeading = areas }
                : new DisplayBlock(badge, areas, string.Empty, style);
            var page = new DisplayPage(0, [block], areas, null);
            var program = new DisplayProgram("caption-badge-alignment", EventId.Create("caption-badge-alignment"),
                kind, SourceMode.ManualTest, Now, OverlayPriority.Quake, [page], Now, EndPolicy.AutoHide,
                "操作テスト／訓練");

            (TelopView view, _, _) = Render(program, 0, AppSettings.CreateDefault().Display);
            Border badgeBorder = FindVisualChildren<Border>(view)
                .Single(border => border.Child is TextBlock label && label.Text == badge && border.ActualHeight > 0);
            var caption = (TextBlock)FindVisualChildren<OutlinedText>(view)
                .Single(text => text.Text == areas && text.ActualHeight > 0).Child;

            Assert.IsGreaterThan(caption.LineHeight, caption.ActualHeight, "本文が折り返される条件で検証します。");
            var badgeLabel = (TextBlock)badgeBorder.Child;
            double firstLineCenter = GetFirstGlyphBaseline(caption, view) - caption.FontSize / 2;
            double badgeLabelCenter = GetFirstGlyphBaseline(badgeLabel, view) - badgeLabel.FontSize / 2;
            Assert.AreEqual(firstLineCenter, badgeLabelCenter, 1.5, "バッジの文字を本文の1行目の文字に揃えます。");
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
                var row = (WrapPanel)VisualTreeHelper.GetParent(text);
                var note = (TextBlock)((OutlinedText)row.Children[1]).Child;
                Assert.IsFalse(string.IsNullOrWhiteSpace(note.Text));
                Assert.AreEqual(GetFirstGlyphBaseline((TextBlock)text.Child, view),
                    GetFirstGlyphBaseline(note, view), 1.5, "到達時刻・高さの基準線を予報区名に揃えます。");
            }
        });
    }

    [TestMethod]
    public void TsunamiArrivalNoteWrapsToTheNextLineWhenItCannotFitBesideTheArea()
    {
        RunSta(() =>
        {
            var area = new OutlinedText { Text = "青森県太平洋沿岸", Width = 500 };
            var note = new OutlinedText
            {
                Text = "〔01時42分 5m〕", Width = 300, FontSize = 34.8, LineHeight = 43.5,
            };
            var panel = new TsunamiCaptionPanel { Children = { area, note } };

            panel.Measure(new Size(600, double.PositiveInfinity));
            panel.Arrange(new Rect(panel.DesiredSize));

            Point noteOrigin = note.TranslatePoint(new Point(), panel);
            Assert.AreEqual(0, noteOrigin.X, 1);
            Assert.AreEqual(area.DesiredSize.Height, noteOrigin.Y, 1);
            Assert.AreEqual(note.DesiredSize.Height, note.ActualHeight, 1);
        });
    }

    private static void AssertEewPrimaryLine(TextBlock text, TelopView view, double expectedTop)
    {
        var parent = (UIElement)VisualTreeHelper.GetParent(text);
        double lineTop = parent.TranslatePoint(LayoutInformation.GetLayoutSlot(text).TopLeft, view).Y;
        Assert.AreEqual(expectedTop, lineTop, 1.5, $"行の配置が一致しません。{text.Text}");

        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        var naturalLine = new FormattedText(text.Text, text.Language.GetSpecificCulture(), text.FlowDirection,
            typeface, text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip);
        double expectedBaseline = lineTop + naturalLine.Baseline + (text.LineHeight - naturalLine.Height) / 2;
        Assert.AreEqual(expectedBaseline, GetFirstGlyphBaseline(text, view), 2, $"行の文字位置が一致しません。{text.Text}");
    }

    private static double GetFirstGlyphBaseline(TextBlock text, Visual view)
    {
        DrawingGroup drawing = VisualTreeHelper.GetDrawing(text);
        GeneralTransform transform = text.TransformToAncestor(view);
        return GetGlyphOrigins(drawing).Min(point => transform.Transform(point).Y);
    }

    private static IEnumerable<Point> GetGlyphOrigins(Drawing drawing)
    {
        if (drawing is GlyphRunDrawing { GlyphRun: { } run })
            yield return run.BaselineOrigin;

        if (drawing is DrawingGroup group)
        {
            foreach (Point origin in group.Children.SelectMany(GetGlyphOrigins))
                yield return group.Transform?.Transform(origin) ?? origin;
        }
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
        if (!string.IsNullOrWhiteSpace(renderDirectory) && program.Kind is EventKind.Eew or EventKind.Quake or EventKind.Tsunami)
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
