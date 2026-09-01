using System.Windows;
using EEWTelop.Wpf.ViewModels;

namespace EEWTelop.Wpf;

public partial class WeatherPrefectureSelectionWindow : Window
{
    private readonly WeatherPrefectureSelectionViewModel _viewModel;

    public WeatherPrefectureSelectionWindow(IEnumerable<string>? selectedCodes)
    {
        InitializeComponent();
        _viewModel = new WeatherPrefectureSelectionViewModel(selectedCodes);
        DataContext = _viewModel;
    }

    public IReadOnlyList<string> SelectedCodes => _viewModel.GetSelectedCodes();

    private void OnSelectAllClicked(object sender, RoutedEventArgs e) =>
        _viewModel.SelectAll();

    private void OnClearAllClicked(object sender, RoutedEventArgs e) =>
        _viewModel.ClearAll();

    private void OnConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsPrefectureSelection && _viewModel.GetSelectedCodes().Length == 0)
        {
            MessageBox.Show(
                this,
                "都道府県を1つ以上選択してください。全国を対象にする場合は「全国」を選択してください。",
                "対象地域",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
