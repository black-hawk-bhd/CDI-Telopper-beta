using EEWTelop.Application.Configuration;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed class TelegramPageDurationViewModel(TelegramPageDurationOption option, double seconds) : ObservableObject
{
    private double _seconds = seconds;

    public string Key => option.Key;
    public string Label => option.Label;
    public double Seconds { get => _seconds; set => SetProperty(ref _seconds, value); }
}
