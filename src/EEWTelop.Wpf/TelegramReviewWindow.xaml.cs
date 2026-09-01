using System.Windows;
using EEWTelop.Wpf.ViewModels;

namespace EEWTelop.Wpf;

public partial class TelegramReviewWindow : Window
{
    public TelegramReviewWindow(ControlWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
