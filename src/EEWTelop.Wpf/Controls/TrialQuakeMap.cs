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
            double scale = Math.Min(1200 / ((East - West) * cosine), 650 / (North - South));
            return new Point(640 + (p.X - (East + West) / 2) * cosine * scale, 445 - (p.Y - (North + South) / 2) * scale);
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
    internal static Extent SelectExtent(IEnumerable<Point> points)
    {
        var all = points.ToArray();
        return all.Length == 0 ? Extents[0] : Extents.Where(e => all.All(e.Contains))
            .OrderBy(e => (e.East - e.West) * (e.North - e.South)).FirstOrDefault() ?? Extents[0];
    }
    public static DrawingImage Render(QuakeEvent quake, string? extentName = null, TrialMapPalette? colors = null)
    {
        var palette = TrialMapPalette.Normalize(colors);
        var text = palette.Brush("Text");
        var matched = quake.Points.Where(p => !quake.IsCancelled && p.Scale != JmaScale.Unknown)
            .Select(p => (Region: Match(p), p.Scale)).Where(p => p.Region is not null).ToArray();
        var scales = matched.GroupBy(p => p.Region!.Code).ToDictionary(g => g.Key, g => g.Max(p => p.Scale));
        var locations = matched.Select(p => p.Region!).Distinct().SelectMany(p => p.Rings.SelectMany(r => r)).ToList();
        var hypo = quake.Earthquake.Hypocenter;
        Point? epicenter = !quake.IsCancelled && hypo?.Longitude is double lon && hypo.Latitude is double lat && InBounds(lon, lat) ? new Point(lon, lat) : null;
        if (epicenter is Point ep) locations.Add(ep);
        Extent extent = Extents.FirstOrDefault(e => e.Name == extentName) ?? SelectExtent(locations);
        var drawing = new DrawingGroup();
        using (DrawingContext dc = drawing.Open())
        {
            dc.DrawRectangle(palette.Brush("Background"), null, new Rect(0, 0, Width, Height));
            Text(dc, "試験地図・選択電文の確認用（自動更新なし）", 25, 18, 26, palette.Brush("Accent"));
            Text(dc, $"{quake.IssuedAt.ToLocalTime():yyyy年M月d日 HH:mm:ss} 発表　{hypo?.Name}　［{extent.Name}］", 25, 58, 23, text);
            if (!quake.IsCancelled && (epicenter is null || !extent.Contains(epicenter.Value)))
                Text(dc, "震央：座標不明または選択範囲外（推測表示しません）", 25, 88, 16, palette.Brush("Accent"));
            dc.PushClip(new RectangleGeometry(new Rect(20, 110, 1240, 675)));
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
                dc.DrawGeometry(fill, new Pen(palette.Brush("Boundary"), .8), geometry);
            }
            var occupied = new List<Point>();
            if (epicenter is Point epic) occupied.Add(extent.Project(epic));
            foreach (var region in Regions.Where(r => scales.ContainsKey(r.Code) && extent.Contains(r.Center)))
            {
                Point origin = extent.Project(region.Center), marker = origin;
                // Displaced labels retain a leader: these are region maxima, not station positions.
                for (int attempt = 0; attempt < 96 && occupied.Any(p => (p - marker).Length < 36); attempt++)
                {
                    double a = attempt * Math.PI / 4, radius = 37 * (1 + attempt / 8);
                    marker = new Point(Math.Clamp(origin.X + Math.Cos(a) * radius, 40, 1240), Math.Clamp(origin.Y + Math.Sin(a) * radius, 135, 760));
                }
                if (occupied.Any(p => (p - marker).Length < 36))
                    marker = (from y in Enumerable.Range(0, 17) from x in Enumerable.Range(0, 33)
                              let candidate = new Point(40 + x * 36, 140 + y * 36)
                              where occupied.All(p => (p - candidate).Length >= 36)
                              orderby (candidate - origin).Length select candidate).First();
                occupied.Add(marker);
                if ((marker - origin).Length > 1) dc.DrawLine(new Pen(text, 1), origin, marker);
                var markerBrush = palette.Scale(scales[region.Code]);
                dc.DrawRectangle(markerBrush, new Pen(text, 1), new Rect(marker.X - 16, marker.Y - 14, 32, 28));
                Text(dc, ScaleLabel(scales[region.Code]), marker.X - 13, marker.Y - 12, 17, TrialMapPalette.Contrast(markerBrush.Color));
            }
            if (epicenter is Point point && extent.Contains(point))
            {
                Point p = extent.Project(point);
                var pen = new Pen(palette.Brush("Epicenter"), 5);
                dc.DrawLine(pen, new(p.X - 10, p.Y - 10), new(p.X + 10, p.Y + 10));
                dc.DrawLine(pen, new(p.X - 10, p.Y + 10), new(p.X + 10, p.Y - 10));
                Text(dc, hypo?.Magnitude is double m ? $"M{m:0.0}" : "M不明", p.X + 15, p.Y + 10, 20, text);
            }
            dc.Pop();
            Text(dc, quake.IsCancelled ? "取消電文：震央・震度を表示しません" : $"地域内最大震度（観測点の位置ではありません）　対応地域 {scales.Count}　未対応・震度不明 {quake.Points.Count - matched.Length}項目", 25, 798, 19, text);
            Text(dc, "5−=5弱  5+=5強  5?=5弱以上未入電  6−=6弱  6+=6強　数字なし：情報なし　×：震央", 25, 829, 18, text);
            Text(dc, "気象庁GIS（地震情報／細分区域）を簡略化・加工。着色は受信値であり、面的な震度推定ではありません。", 25, 859, 17, text);
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing); image.Freeze(); return image;
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
