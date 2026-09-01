using System.Windows;
using EEWTelop.Wpf.ViewModels;

namespace EEWTelop.Wpf;

public partial class PreviewWindow : Window
{
    public PreviewWindow(OverlayViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }
}
