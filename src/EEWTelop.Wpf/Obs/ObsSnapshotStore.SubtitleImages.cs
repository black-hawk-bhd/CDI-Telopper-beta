using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;

namespace EEWTelop.Wpf.Obs;

public sealed record ObsSubtitleImage(string Id, int X, int Y, int Width);

public sealed partial class ObsSnapshotStore
{
    private Dictionary<EventKind, (ObsSubtitleImage Placement, SubtitlePng Image)> _subtitleImages = [];

    private static Dictionary<EventKind, (ObsSubtitleImage Placement, SubtitlePng Image)> PrepareSubtitleImages(DisplaySettings settings)
    {
        var images = new Dictionary<EventKind, (ObsSubtitleImage, SubtitlePng)>();
        foreach (var (kind, image) in settings.SubtitleImages ?? [])
        {
            if (kind is EventKind.Unknown || !Enum.IsDefined(kind) || image is null ||
                SubtitlePng.FromBase64(image.PngBase64) is not { } png)
                continue;

            SubtitleImageSettings placement = image.NormalizePlacement();
            images.Add(kind, (new ObsSubtitleImage(png.Id, placement.X, placement.Y, placement.Width), png));
        }

        return images;
    }

    private ObsSubtitleImage? GetSubtitleImage(EventKind? kind) =>
        kind is { } category && _subtitleImages.TryGetValue(category, out var image) ? image.Placement : null;

    internal ReadOnlyMemory<byte>? ReadSubtitleImage(string id)
    {
        lock (_gate)
        {
            foreach (var image in _subtitleImages.Values)
            {
                if (string.Equals(image.Image.Id, id, StringComparison.Ordinal))
                    return image.Image.Data;
            }

            return null;
        }
    }
}
