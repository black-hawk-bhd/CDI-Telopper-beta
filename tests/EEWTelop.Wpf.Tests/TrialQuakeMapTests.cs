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
    public void MunicipalityPointsResolvePrefectureAndExactNamesWithoutGuessingStations()
    {
        var p = new QuakePoint("宮崎県", "宮崎都農町", false, JmaScale.Two, "宮崎県宮崎都農町");
        var location = TrialMunicipalityPoints.Resolve(p);
        Assert.IsNotNull(location);
        Assert.AreEqual("city:4540600", location.Code);
        Assert.IsTrue(TrialQuakeMap.Extents.Single(e => e.Name == "九州").Contains(location.Position));
        Assert.IsNotNull(TrialMunicipalityPoints.Resolve(p with { Address = "宮崎美郷町" }));
        Assert.IsNotNull(TrialMunicipalityPoints.Resolve(p with { Address = "宮崎県日南市" }));
        Assert.IsNull(TrialMunicipalityPoints.Resolve(p with { Address = "日南市油津" }));
        Assert.IsNull(TrialMunicipalityPoints.Resolve(p with { Prefecture = "大分県", Address = "日南市" }));
        Assert.IsNull(TrialMunicipalityPoints.Resolve(p with { Prefecture = "" }));
        foreach (var (prefecture, city) in new[] { ("神奈川県", "横浜市"), ("神奈川県", "相模原市"),
            ("北海道", "北見市"), ("北海道", "釧路市"), ("宮城県", "仙台市") })
        {
            Assert.IsNotNull(TrialMunicipalityPoints.Resolve(p with { Prefecture = prefecture, Address = city }));
        }
        Assert.IsNull(TrialMunicipalityPoints.Resolve(p with { Prefecture = "神奈川県", Address = "横浜市中区山手町" }));
    }

    [TestMethod]
    public void MunicipalityMapRowsRetainMaximumAndRender()
    {
        var sample = (QuakeEvent)TestScenarioCatalog.Create(DateTimeOffset.UtcNow).Single(s => s.Id == "detail-scale").Event;
        var p = new QuakePoint("宮崎県", "都農町", false, JmaScale.One, "宮崎県都農町");
        var quake = new QuakeEvent(sample.Id, sample.Provider, sample.IssuedAt, sample.ReceivedAt, sample.Signature,
            sample.SourceMode, sample.Issue, sample.IssueType, sample.Earthquake, [p, p with { Scale = JmaScale.Two }], "");
        var rows = TrialQuakeMap.GetRegionRows(quake);
        Assert.HasCount(1, rows);
        Assert.IsTrue(rows[0].Mapped);
        Assert.AreEqual(JmaScale.Two, rows[0].Scale);
        Assert.IsTrue(rows[0].DisplayText.Contains("市町村代表点", StringComparison.Ordinal));
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                DrawingImage image = TrialQuakeMap.Render(quake, "九州", selectedRegionCode: rows[0].Code);
                Assert.IsTrue(image.IsFrozen);
                var crowded = (QuakeEvent)TestScenarioCatalog.Create(DateTimeOffset.UtcNow)
                    .Single(s => s.Id == "large-5-lower").Event;
                var crowdedRows = TrialQuakeMap.GetRegionRows(crowded);
                Assert.IsTrue(crowdedRows.All(r => r.Mapped), string.Join(",", crowdedRows.Where(r => !r.Mapped).Select(r => r.Name)));
                var candidates = crowded.Points.Select(p => TrialMunicipalityPoints.Resolve(p)!)
                    .Select(p => new TrialQuakeMap.LabelCandidate(p.Code, JmaScale.Three, new Point(100, 100)));
                Assert.HasCount(crowded.Points.Count, TrialQuakeMap.PlaceLabels(candidates, new Rect(0, 0, 900, 700), null, null));
                image = TrialQuakeMap.Render(crowded);
                string? output = Environment.GetEnvironmentVariable("EEWTELOP_RENDER_OUTPUT");
                if (!string.IsNullOrEmpty(output))
                {
                    Directory.CreateDirectory(output);
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen()) dc.DrawImage(image, new Rect(0, 0, 1280, 900));
                    var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, "municipality-map.png"));
                    encoder.Save(stream);
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    [TestMethod]
    public void CrowdedLabelsAllRemainAtOriginWithStrongestAndSelectionOnTop()
    {
        TrialQuakeMap.LabelCandidate[] candidates = [new("low", JmaScale.One, new Point(100, 100)), new("high", JmaScale.Seven, new Point(105, 100)), new("epic", JmaScale.SixUpper, new Point(200, 200))];
        var bounds = new Rect(0, 0, 400, 400);
        var obstacle = new Rect(182, 182, 36, 36);
        var placed = TrialQuakeMap.PlaceLabels(candidates, bounds, obstacle, null);
        Assert.HasCount(3, placed); Assert.AreEqual("high", placed[^1].Code);
        Assert.AreEqual(candidates[1].Center, placed[^1].Center);
        placed = TrialQuakeMap.PlaceLabels(candidates.Reverse(), bounds, obstacle, "low");
        Assert.HasCount(3, placed); Assert.AreEqual("low", placed[^1].Code);
        Assert.HasCount(1, TrialQuakeMap.PlaceLabels([new("edge", JmaScale.Seven, new Point(0, 0))], bounds, null, null));
        Assert.IsEmpty(TrialQuakeMap.PlaceLabels([new("outside", JmaScale.Seven, new Point(-1, 0))], bounds, null, null));
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
