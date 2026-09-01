using System.Windows;
using EEWTelop.Application.Display;
using EEWTelop.Wpf.ViewModels;

namespace EEWTelop.Wpf;

public partial class SubtitleEditorWindow : Window
{
    private readonly SubtitleEditorViewModel _viewModel;

    public SubtitleEditorWindow(
        DisplayProgram sourceProgram,
        DisplayProgram displayedProgram,
        bool releaseAfterEditing = false)
    {
        ArgumentNullException.ThrowIfNull(sourceProgram);
        ArgumentNullException.ThrowIfNull(displayedProgram);
        InitializeComponent();
        _viewModel = new SubtitleEditorViewModel(sourceProgram, displayedProgram);
        DataContext = _viewModel;
        if (releaseAfterEditing)
        {
            Title = "作画前の字幕を編集・送出";
            EditorHeading.Text = "作画前の字幕をページ単位で編集します";
            ApplyButton.Content = "編集して送出";
        }
    }

    public DisplayProgram EditedProgram => _viewModel.BuildEditedProgram();

    private void OnResetClicked(object sender, RoutedEventArgs e) => _viewModel.Reset();

    private void OnApplyClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
