using EEWTelop.Application.Configuration;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed class SubtitleImageEditor : ObservableObject
{
    private string _pngBase64;
    private string _fileName;
    private int _x;
    private int _y;
    private int _width;

    public SubtitleImageEditor(EventKind kind, string label, SubtitleImageSettings? settings)
    {
        Kind = kind;
        Label = label;
        SubtitleImageSettings initial = (settings ?? new SubtitleImageSettings(string.Empty, string.Empty)).NormalizePlacement();
        _fileName = initial.FileName ?? string.Empty;
        _pngBase64 = initial.PngBase64 ?? string.Empty;
        _x = initial.X;
        _y = initial.Y;
        _width = initial.Width;
    }

    public EventKind Kind { get; }
    public string Label { get; }
    public bool HasImage => _pngBase64.Length > 0;
    public string FileLabel => HasImage ? _fileName : "未登録";
    public int X { get => _x; set => SetProperty(ref _x, value); }
    public int Y { get => _y; set => SetProperty(ref _y, value); }
    public int Width { get => _width; set => SetProperty(ref _width, value); }

    internal void SetImage(string fileName, byte[] bytes) => SetImage(fileName, Convert.ToBase64String(bytes));

    internal void ClearImage() => SetImage(string.Empty, string.Empty);

    internal SubtitleImageSettings ToSettings() =>
        new SubtitleImageSettings(_fileName, _pngBase64, X, Y, Width).NormalizePlacement();

    private void SetImage(string fileName, string pngBase64)
    {
        _fileName = fileName;
        _pngBase64 = pngBase64;
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(FileLabel));
    }
}
