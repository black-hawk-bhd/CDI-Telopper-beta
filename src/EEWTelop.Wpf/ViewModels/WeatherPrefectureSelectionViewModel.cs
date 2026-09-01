using System.Collections.ObjectModel;
using EEWTelop.Application.Configuration;
using EEWTelop.Wpf.Mvvm;

namespace EEWTelop.Wpf.ViewModels;

public sealed class WeatherPrefectureSelectionViewModel : ObservableObject
{
    private bool _isNationwide;

    public WeatherPrefectureSelectionViewModel(IEnumerable<string>? selectedCodes)
    {
        HashSet<string> selected = WeatherPrefectureCatalog
            .NormalizeCodes(selectedCodes)
            .ToHashSet(StringComparer.Ordinal);
        _isNationwide = selected.Count == 0;
        Prefectures = new ObservableCollection<WeatherPrefectureSelectionItemViewModel>(
            WeatherPrefectureCatalog.Options
                .Where(static option => !string.IsNullOrEmpty(option.Code))
                .Select(option => new WeatherPrefectureSelectionItemViewModel(
                    option.Code,
                    option.Name,
                    selected.Contains(option.Code))));
    }

    public ObservableCollection<WeatherPrefectureSelectionItemViewModel> Prefectures { get; }

    public bool IsNationwide
    {
        get => _isNationwide;
        set
        {
            if (SetProperty(ref _isNationwide, value))
            {
                OnPropertyChanged(nameof(IsPrefectureSelection));
                OnPropertyChanged(nameof(IsPrefectureListEnabled));
            }
        }
    }

    public bool IsPrefectureSelection
    {
        get => !_isNationwide;
        set => IsNationwide = !value;
    }

    public bool IsPrefectureListEnabled => !_isNationwide;

    public string[] GetSelectedCodes() => IsNationwide
        ? []
        : Prefectures
            .Where(static prefecture => prefecture.IsSelected)
            .Select(static prefecture => prefecture.Code)
            .ToArray();

    public void SelectAll()
    {
        IsPrefectureSelection = true;
        foreach (WeatherPrefectureSelectionItemViewModel prefecture in Prefectures)
        {
            prefecture.IsSelected = true;
        }
    }

    public void ClearAll()
    {
        foreach (WeatherPrefectureSelectionItemViewModel prefecture in Prefectures)
        {
            prefecture.IsSelected = false;
        }
    }
}

public sealed class WeatherPrefectureSelectionItemViewModel : ObservableObject
{
    private bool _isSelected;

    public WeatherPrefectureSelectionItemViewModel(string code, string name, bool isSelected)
    {
        Code = code;
        Name = name;
        _isSelected = isSelected;
    }

    public string Code { get; }

    public string Name { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
