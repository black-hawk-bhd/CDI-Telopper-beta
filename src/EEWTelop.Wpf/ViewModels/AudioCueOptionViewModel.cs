using EEWTelop.Application.Audio;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed class AudioCueOptionViewModel : ObservableObject
{
    private bool _enabled;
    private string _filePath;

    public AudioCueOptionViewModel(
        AudioCueId cue,
        string label,
        bool enabled,
        string filePath,
        JmaScale? quakeScale,
        TsunamiGrade? tsunamiGrade)
    {
        Cue = cue;
        Label = label;
        _enabled = enabled;
        _filePath = filePath;
        QuakeScale = quakeScale;
        TsunamiGrade = tsunamiGrade;
    }

    public AudioCueId Cue { get; }

    public string Label { get; }

    public JmaScale? QuakeScale { get; }

    public TsunamiGrade? TsunamiGrade { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value ?? string.Empty);
    }
}
