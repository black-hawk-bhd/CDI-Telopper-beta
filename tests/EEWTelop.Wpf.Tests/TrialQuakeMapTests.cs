using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EEWTelop.Application.Testing;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EEWTelop.Wpf.Tests;

[TestClass]
public sealed class TrialQuakeMapTests
{
    [TestMethod]
    public void CrowdedLabelsStayAtOriginAndRespectPriorityAndEpicenter()
    {
        TrialQuakeMap.LabelCandidate[] candidates = [new("low", JmaScale.One, new Point(100, 100)), new("high", JmaScale.Seven, new Point(105, 100)), new("epic", JmaScale.SixUpper, new Point(200, 200))];
        var bounds = new Rect(0, 0, 400, 400);
        var obstacle = new Rect(182, 182, 36, 36);
        var placed = TrialQuakeMap.PlaceLabels(candidates, bounds, obstacle, null);
        Assert.HasCount(1, placed); Assert.AreEqual("high", placed[0].Code);
        Assert.AreEqual(candidates[1].Center, placed[0].Center);
        placed = TrialQuakeMap.PlaceLabels(candidates.Reverse(), bounds, obstacle, "low");
        Assert.HasCount(1, placed); Assert.AreEqual("low", placed[0].Code);
        Assert.IsEmpty(TrialQuakeMap.PlaceLabels([new("edge", JmaScale.Seven, new Point(0, 0))], bounds, null, null));
    }

    [TestMethod]
    public void RegionMatchingUsesCodeFirstAndNeverGuessesMunicipality()
    {
        var p = new QuakePoint("石川県", "珠洲市", false, JmaScale.Four, "石川県珠洲市");
        Assert.IsNull(TrialQuakeMap.ResolveAreaCode(p));
        Assert.AreEqual("390", TrialQuakeMap.ResolveAreaCode(p with { SeismicAreaCode = "390" }));
        Assert.AreEqual("390", TrialQuakeMap.ResolveAreaCode(p with { SeismicAreaName = "石川県能登" }));
        Assert.IsNull(TrialQuakeMap.ResolveAreaCode(p with { SeismicAreaCode = "invalid", SeismicAreaName = "石川県能登" }));
    }

    [TestMethod]
    public void FixedViewSelectionHandlesEmptyRegionalAndDistantLocations()
    {
        Assert.AreEqual("全国", TrialQuakeMap.SelectExtent([]).Name);
        Assert.AreEqual("関東・甲信", TrialQuakeMap.SelectExtent([new Point(140, 36)]).Name);
        Assert.AreEqual("沖縄", TrialQuakeMap.SelectExtent([new Point(124, 24)]).Name);
        Assert.AreEqual("東日本", TrialQuakeMap.SelectExtent([new Point(140, 36), new Point(145, 44)]).Name);
        Assert.AreEqual("全国", TrialQuakeMap.SelectExtent([new Point(170, 0)]).Name);
    }

    [TestMethod]
    public void FixedExtentAndRenderedFrameAreBounded()
    {
        Assert.IsFalse(TrialQuakeMap.InBounds(double.NaN, 35));
        Assert.IsFalse(TrialQuakeMap.InBounds(110, 35));
        foreach (var coordinate in new[] { (122d, 20d), (154d, 47d) })
        {
            Point p = TrialQuakeMap.Project(coordinate.Item1, coordinate.Item2);
            Assert.IsTrue(p.X > 0 && p.X < TrialQuakeMap.Width && p.Y > 0 && p.Y < 820);
        }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var quake = (QuakeEvent)TestScenarioCatalog.Create(DateTimeOffset.UtcNow).Single(s => s.Id == "detail-scale").Event;
                quake = new QuakeEvent(quake.Id, quake.Provider, quake.IssuedAt, quake.ReceivedAt, quake.Signature, quake.SourceMode,
                    quake.Issue, quake.IssueType, quake.Earthquake,
                    [new QuakePoint("東京都", "東京都２３区", true, JmaScale.FiveUpper, "東京都２３区"),
                     new QuakePoint("神奈川県", "神奈川県東部", true, JmaScale.Four, "神奈川県東部"),
                     new QuakePoint("千葉県", "千葉県北西部", true, JmaScale.SixLower, "千葉県北西部")], "");
                DrawingImage image = TrialQuakeMap.Render(quake);
                var rows = TrialQuakeMap.GetRegionRows(quake);
                Assert.HasCount(3, rows);
                Assert.AreEqual(JmaScale.SixLower, rows[0].Scale);
                Assert.AreEqual("千葉県北西部", rows[0].Name);
                Assert.AreEqual(JmaScale.Four, rows[2].Scale);
                var cancelled = new QuakeEvent(quake.Id, quake.Provider, quake.IssuedAt, quake.ReceivedAt, quake.Signature, quake.SourceMode,
                    quake.Issue, quake.IssueType, quake.Earthquake, quake.Points, "", isCancelled: true);
                Assert.IsEmpty(TrialQuakeMap.GetRegionRows(cancelled));
                Assert.IsTrue(image.IsFrozen);
                Assert.AreEqual(1280d, image.Width);
                Assert.AreEqual(900d, image.Height);
                string? output = Environment.GetEnvironmentVariable("EEWTELOP_RENDER_OUTPUT");
                if (output is not null)
                {
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen()) dc.DrawImage(image, new Rect(0, 0, 1280, 900));
                    var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(output);
                    using var file = File.Create(Path.Combine(output, "trial-quake-map.png"));
                    encoder.Save(file);
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
