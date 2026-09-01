using System.Windows;
using EEWTelop.Application.Display;
using EEWTelop.Wpf.ViewModels;

namespace EEWTelop.Wpf;

public partial class PendingSubtitleSelectionWindow : Window
{
    private readonly PendingSubtitleSelectionViewModel _viewModel;

    public PendingSubtitleSelectionWindow(IEnumerable<DisplayProgram> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        InitializeComponent();
        _viewModel = new PendingSubtitleSelectionViewModel(programs);
        DataContext = _viewModel;
    }

    public DisplayProgram? SelectedProgram => _viewModel.SelectedItem?.Program;

    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedProgram is null)
        {
            MessageBox.Show(
                this,
                "編集する字幕を選択してください。",
                "表示前の字幕を編集",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
