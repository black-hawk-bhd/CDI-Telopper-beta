using System.Windows;
using EEWTelop.Wpf.ViewModels;

namespace EEWTelop.Wpf;

public partial class SubtitlePhraseTemplateWindow : Window
{
    private readonly SubtitlePhraseTemplateViewModel _viewModel;

    public SubtitlePhraseTemplateWindow(
        IReadOnlyDictionary<string, string>? overrides)
    {
        InitializeComponent();
        _viewModel = new SubtitlePhraseTemplateViewModel(overrides);
        DataContext = _viewModel;
    }

    public IReadOnlyDictionary<string, string> PhraseOverrides =>
        _viewModel.BuildOverrides();

    private void OnResetClicked(object sender, RoutedEventArgs e) =>
        _viewModel.ResetAll();

    private void OnApplyClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
