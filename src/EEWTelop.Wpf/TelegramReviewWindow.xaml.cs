using System.Windows;
using EEWTelop.Wpf.ViewModels;
using EEWTelop.Domain.Events;

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

    private void OnTrialMapClicked(object sender, RoutedEventArgs e)
    {
        if (!BuildFeatures.TrialMapEnabled) return;
        if (DataContext is not ControlWindowViewModel vm || vm.SelectedReceivedTelegram?.Event is not QuakeEvent quake)
        {
            MessageBox.Show(this, "地震情報の電文を選択してください。EEW・津波・気象情報は対象外です。", "地図確認");
            return;
        }
        vm.RequestMapReview(quake);
    }
}
