namespace EEWTelop.Application.Configuration;

public sealed record SubtitleImageSettings(
    string FileName,
    string PngBase64,
    int X = 24,
    int Y = 780,
    int Width = 192)
{
    public SubtitleImageSettings NormalizePlacement() => this with
    {
        X = Math.Clamp(X, 0, 1919),
        Y = Math.Clamp(Y, 0, 1079),
        Width = Math.Clamp(Width, 1, 1920),
    };
}
