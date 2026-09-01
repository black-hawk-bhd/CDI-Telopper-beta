using System.Windows;
using EEWTelop.Wpf.Bootstrap;

namespace EEWTelop.Wpf;

public partial class MainWindow : Window
{
    private readonly AppServices _services;

    public MainWindow()
    {
        InitializeComponent();
        _services = AppComposition.CreateDefault();
        ProviderText.Text = _services.Provider.WebSocketUri.ToString();
        ClockText.Text = _services.Clock.UtcNow.ToString("O");
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await _services.DisposeAsync();
    }
}
