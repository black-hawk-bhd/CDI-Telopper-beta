using EEWTelop.Application.Formatting;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed class QuakeIntensityFilterOptionViewModel : ObservableObject
{
    private bool _isExcluded;

    public QuakeIntensityFilterOptionViewModel(JmaScale scale, bool isExcluded)
    {
        Scale = scale;
        _isExcluded = isExcluded;
    }

    public JmaScale Scale { get; }
    public string Label => "震度" + ScaleFormatter.Format(Scale);
    public bool IsExcluded { get => _isExcluded; set => SetProperty(ref _isExcluded, value); }
}
