using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Controls;

internal static class TrialQuakeMap
{
    public const int Width = 1280, Height = 900;
    internal sealed record Extent(string Name, double West, double South, double East, double North)
    {
        public bool Contains(Point p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X >= West && p.X <= East && p.Y >= South && p.Y <= North;
        public Point Project(Point p)
        {
            double cosine = Math.Cos((North + South) / 2 * Math.PI / 180);
            double scale = Math.Min(860 / ((East - West) * cosine), 650 / (North - South));
            return new Point(460 + (p.X - (East + West) / 2) * cosine * scale, 445 - (p.Y - (North + South) / 2) * scale);
        }
    }
    internal static readonly Extent[] Extents =
    [
        new("全国", 122, 20, 154, 47), new("北海道", 139, 40, 147, 46.5),
        new("東北", 138, 36, 144, 42), new("関東・甲信", 137, 33, 142.5, 38),
        new("東海・北陸", 134.5, 33, 140, 38.5), new("近畿", 133, 32.5, 138, 36.5),
        new("中国・四国", 130, 31.5, 136, 36.5), new("九州", 128, 29, 133, 35),
        new("沖縄", 122, 23, 132, 29), new("東日本", 136, 30, 148, 47), new("西日本", 127, 28, 139, 38),
    ];
    private sealed record Region(string Code, string Name, Point Center, Point[][] Rings);
    private static readonly Region[] Regions = Load();
    internal static Point Project(double longitude, double latitude) => Extents[0].Project(new(longitude, latitude));
    internal static bool InBounds(double longitude, double latitude) => Extents[0].Contains(new(longitude, latitude));
    private static Region? Match(QuakePoint p) => !string.IsNullOrEmpty(p.SeismicAreaCode)
        ? Regions.FirstOrDefault(r => r.Code == p.SeismicAreaCode)
        : Regions.FirstOrDefault(r => r.Name == (p.IsArea ? p.Address : p.SeismicAreaName));
    internal static string? ResolveAreaCode(QuakePoint p) => Match(p)?.Code;
    internal sealed record RegionRow(string Code, string Name, JmaScale Scale, bool Mapped)
    {
        public string DisplayText => $"{(Scale == JmaScale.Unknown ? "不明" : ScaleLabel(Scale))}　{Name}{(Mapped ? "" : "（地図未対応）")}";
    }
    internal static RegionRow[] GetRegionRows(QuakeEvent quake) => quake.IsCancelled ? [] : quake.Points
        .Select(p => TrialMunicipalityPoints.Resolve(p) is { } city
            ? new RegionRow(city.Code, city.Prefecture + city.Name + "（市町村代表点）", p.Scale, true)
            : Match(p) is { } r ? new RegionRow(r.Code, r.Name, p.Scale, true)
            : new RegionRow("", p.DisplayName, p.Scale, false))
        .GroupBy(r => (r.Code, r.Name, r.Mapped))
        .Select(g => g.First() with { Scale = g.Max(r => r.Scale) })
        .OrderByDescending(r => r.Scale).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();

    internal sealed record LabelCandidate(string Code, JmaScale Scale, Point Center);
    internal static LabelCandidate[] PlaceLabels(IEnumerable<LabelCandidate> candidates, Rect bounds, Rect? reserved, string? selected)
    {
        // Draw every in-view point; stronger intensities and the selection are painted last.
        // The epicenter is drawn afterward instead of hiding nearby observations.
        return candidates.Where(c => bounds.Contains(c.Center))
            .OrderBy(c => c.Code == selected).ThenBy(c => c.Scale)
            .ThenBy(c => c.Code, StringComparer.Ordinal).ToArray();
    }
    internal static Extent SelectExtent(IEnumerable<Point> points)
    {
        var all = points.ToArray();
        return all.Length == 0 ? Extents[0] : Extents.Where(e => all.All(e.Contains))
            .OrderBy(e => (e.East - e.West) * (e.North - e.South)).FirstOrDefault() ?? Extents[0];
    }
    public static DrawingImage Render(QuakeEvent quake, string? extentName = null, TrialMapPalette? colors = null, string? selectedRegionCode = null)
    {
        var palette = TrialMapPalette.Normalize(colors);
        var text = palette.Brush("Text");
        var cities = quake.Points.Where(p => !quake.IsCancelled && p.Scale != JmaScale.Unknown)
            .Select(p => (City: TrialMunicipalityPoints.Resolve(p), p.Scale)).Where(p => p.City is not null)
            .GroupBy(p => p.City!.Code).Select(g => (City: g.First().City!, Scale: g.Max(p => p.Scale))).ToArray();
        var matched = quake.Points.Where(p => !quake.IsCancelled && p.Scale != JmaScale.Unknown)
            .Select(p => (Region: Match(p), p.Scale)).Where(p => p.Region is not null).ToArray();
        var scales = matched.GroupBy(p => p.Region!.Code).ToDictionary(g => g.Key, g => g.Max(p => p.Scale));
        var locations = matched.Select(p => p.Region!).Distinct().SelectMany(p => p.Rings.SelectMany(r => r)).ToList();
        locations.AddRange(cities.Select(p => p.City.Position));
        var hypo = quake.Earthquake.Hypocenter;
        Point? epicenter = !quake.IsCancelled && hypo?.Longitude is double lon && hypo.Latitude is double lat && InBounds(lon, lat) ? new Point(lon, lat) : null;
        if (epicenter is Point ep) locations.Add(ep);
        Extent extent = Extents.FirstOrDefault(e => e.Name == extentName) ?? SelectExtent(locations);
        var candidates = Regions.Where(r => scales.ContainsKey(r.Code) && extent.Contains(r.Center))
            .Select(r => new LabelCandidate(r.Code, scales[r.Code], extent.Project(r.Center)))
            .Concat(cities.Where(p => extent.Contains(p.City.Position))
                .Select(p => new LabelCandidate(p.City.Code, p.Scale, extent.Project(p.City.Position)))).ToArray();
        Rect? reserved = epicenter is Point epic && extent.Contains(epic)
            ? new Rect(extent.Project(epic).X - 18, extent.Project(epic).Y - 18, 36, 36) : null;
        var labels = PlaceLabels(candidates, new Rect(20, 110, 880, 675), reserved, selectedRegionCode);
        var drawing = new DrawingGroup();
        using (DrawingContext dc = drawing.Open())
        {
            dc.DrawRectangle(palette.Brush("Background"), null, new Rect(0, 0, Width, Height));
            Text(dc, quake.SourceMode is SourceMode.ManualTest or SourceMode.Sandbox
                ? "【訓練・試験電文】実際の地震情報ではありません"
                : "試験地図・選択電文の確認用（自動更新なし）", 25, 18, 26, palette.Brush("Accent"));
            Text(dc, $"{quake.IssuedAt.ToLocalTime():yyyy年M月d日 HH:mm:ss} 発表　{hypo?.Name}　［{extent.Name}］", 25, 58, 23, text);
            if (!quake.IsCancelled && (epicenter is null || !extent.Contains(epicenter.Value)))
                Text(dc, "震央：座標不明または選択範囲外（推測表示しません）", 25, 88, 16, palette.Brush("Accent"));
            else if (!quake.IsCancelled)
                Text(dc, "×：震央　一覧で地域を選択するとラベルを優先表示します", 25, 88, 16, text);
            dc.PushClip(new RectangleGeometry(new Rect(20, 110, 880, 675)));
            var grid = new Pen(palette.Brush("Grid"), 1);
            if (palette.ShowGrid)
            {
                for (int x = 0; x <= Width; x += 60) dc.DrawLine(grid, new(x, 110), new(x, 785));
                for (int y = 110; y <= 785; y += 60) dc.DrawLine(grid, new(20, y), new(1260, y));
            }
            foreach (var region in Regions)
            {
                var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (var path = geometry.Open())
                    foreach (var ring in region.Rings.Where(r => r.Length >= 3))
                    {
                        path.BeginFigure(extent.Project(ring[0]), true, true);
                        path.PolyLineTo(ring.Skip(1).Select(extent.Project).ToArray(), true, false);
                    }
                geometry.Freeze();
                Brush fill = palette.Brush("Land");
                if (scales.TryGetValue(region.Code, out var scale))
                {
                    var c = palette.Scale(scale).Color;
                    var background = palette.Brush("Land").Color;
                    fill = new SolidColorBrush(Color.FromRgb((byte)(c.R * .65 + background.R * .35), (byte)(c.G * .65 + background.G * .35), (byte)(c.B * .65 + background.B * .35)));
                }
                dc.DrawGeometry(fill, new Pen(region.Code == selectedRegionCode ? palette.Brush("Accent") : palette.Brush("Boundary"), region.Code == selectedRegionCode ? 2.5 : .8), geometry);
            }
            foreach (var label in labels)
            {
                Point marker = label.Center;
                var markerBrush = palette.Scale(label.Scale);
                if (label.Code == selectedRegionCode)
                    dc.DrawEllipse(null, new Pen(palette.Brush("Accent"), 3), marker, 18, 18);
                dc.DrawEllipse(markerBrush, new Pen(Brushes.White, 2), marker, 15, 15);
                var number = new FormattedText(ScaleLabel(label.Scale), CultureInfo.GetCultureInfo("ja-JP"),
                    FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), 17,
                    TrialMapPalette.Contrast(markerBrush.Color), 1);
                dc.DrawText(number, new Point(marker.X - number.Width / 2, marker.Y - number.Height / 2));
            }
            if (epicenter is Point point && extent.Contains(point))
            {
                Point p = extent.Project(point);
                var pen = new Pen(palette.Brush("Epicenter"), 5);
                dc.DrawLine(pen, new(p.X - 10, p.Y - 10), new(p.X + 10, p.Y + 10));
                dc.DrawLine(pen, new(p.X - 10, p.Y + 10), new(p.X + 10, p.Y - 10));
            }
            dc.Pop();
            DrawSummary(dc, quake, palette);
            Text(dc, quake.IsCancelled ? "取消電文：震央・震度を表示しません" : $"地域内最大震度 {scales.Count}地域／市町村代表点 {cities.Length}地点（観測点位置ではありません）", 25, 798, 19, text);
            Text(dc, $"全地点描画：{labels.Length}　範囲外：{scales.Count + cities.Length - candidates.Length}　未対応：{GetRegionRows(quake).Count(r => !r.Mapped)}　重なりは地方図・一覧選択で確認", 25, 829, 18, text);
            Text(dc, "気象庁GIS（地震情報／細分区域）を簡略化・加工。着色は受信値であり、面的な震度推定ではありません。", 25, 859, 17, text);
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing); image.Freeze(); return image;
    }
    private static void DrawSummary(DrawingContext dc, QuakeEvent quake, TrialMapPalette palette)
    {
        var summary = MapEarthquakeSummary.Create(quake);
        var group = new DrawingGroup();
        double y = 137;
        using (var content = group.Open())
        {
            void Line(string value, double size, Brush color, int maxLines = 4)
            {
                var formatted = new FormattedText(value, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight,
                    new Typeface("Yu Gothic UI"), size, color, 1)
                { MaxTextWidth = 300, MaxLineCount = maxLines, Trimming = maxLines == int.MaxValue ? TextTrimming.None : TextTrimming.CharacterEllipsis };
                content.DrawText(formatted, new Point(940, y)); y += formatted.Height + 9;
            }
            Line("選択電文の地震情報", 23, palette.Brush("Accent"));
            Line("震源地", 16, palette.Brush("Accent"));
            Line(summary.Hypocenter, 26, palette.Brush("Text"));
            Line("マグニチュード", 16, palette.Brush("Accent"));
            Line(summary.Magnitude, 30, palette.Brush("Text"));
            Line("最大震度", 16, palette.Brush("Accent"));
            Line(summary.MaximumIntensity, 28, palette.Brush("Text"));
            Line("津波情報", 16, palette.Brush("Accent"));
            Line(summary.Tsunami, 19, palette.Brush("Text"), int.MaxValue);
            Line("発表時点の情報です。\n最新の津波情報を確認してください。", 14, palette.Brush("Accent"));
        }
        double height = y - 110;
        dc.DrawRoundedRectangle(palette.Brush("Background"), new Pen(palette.Brush("Boundary"), 1.5), new Rect(925, 120, 330, Math.Min(650, height)), 8, 8);
        double fit = Math.Min(1, 650 / height);
        dc.PushTransform(new ScaleTransform(fit, fit, 925, 120));
        dc.DrawDrawing(group);
        dc.Pop();
    }
    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("ja-JP"), FlowDirection.LeftToRight, new Typeface("Yu Gothic UI"), size, brush, 1)
        { MaxTextWidth = Math.Max(1, Width - x - 5), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(formatted, new Point(x, y));
    }
    private static string ScaleLabel(JmaScale scale) => scale switch
    {
        JmaScale.FiveLower => "5−", JmaScale.FiveLowerOrMore => "5?", JmaScale.FiveUpper => "5+",
        JmaScale.SixLower => "6−", JmaScale.SixUpper => "6+", _ => ((int)scale / 10).ToString(CultureInfo.InvariantCulture),
    };
    private static Region[] Load()
    {
        using var stream = typeof(TrialQuakeMap).Assembly.GetManifestResourceStream("EEWTelop.Wpf.Assets.trial-seismic-regions.json") ?? throw new InvalidOperationException("Trial map data missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("regions").EnumerateArray().Select(r => new Region(r.GetProperty("code").GetString()!, r.GetProperty("name").GetString()!,
            new Point(r.GetProperty("center")[0].GetDouble(), r.GetProperty("center")[1].GetDouble()),
            r.GetProperty("polygons").EnumerateArray().Select(ring => ring.EnumerateArray().Select(p => new Point(p[0].GetDouble(), p[1].GetDouble())).ToArray()).ToArray())).ToArray();
    }
}
