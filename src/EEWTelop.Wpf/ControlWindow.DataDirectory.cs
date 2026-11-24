using System.IO;
using System.Windows;
using System.Windows.Controls;
using EEWTelop.Infrastructure.Settings;
using EEWTelop.Wpf.Bootstrap;

namespace EEWTelop.Wpf;

public partial class ControlWindow
{
    private DataDirectorySelection? _dataLocation;

    private void InitializeDataDirectory(AppServices services)
    {
        CurrentDataDirectory.Text = services.DataDirectory;
        _dataLocation = services.DataLocation;
        DataDirectoryChoice.IsEnabled = _dataLocation?.CanChange == true;
        if (_dataLocation?.CanChange == true)
        {
            DataDirectoryChoice.SelectedIndex = _dataLocation.UsePortableOnNextLaunch ? 1 : 0;
        }
        else
        {
            DataDirectoryChoice.SelectedIndex = -1;
            DataDirectoryStatus.Text = "保存先は環境変数などで指定されています。";
        }
    }

    private void OnDataDirectoryChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_dataLocation is { CanChange: true } && DataDirectoryChoice.IsEnabled)
        {
            UpdateDataDirectoryButton();
        }
    }

    private void UpdateDataDirectoryButton()
    {
        ApplyDataDirectoryButton.IsEnabled = _dataLocation is { CanChange: true } location &&
            (DataDirectoryChoice.SelectedIndex == 1) != location.UsePortableOnNextLaunch;
    }

    private async void OnApplyDataDirectory(object sender, RoutedEventArgs e)
    {
        if (_dataLocation is not { CanChange: true } location)
        {
            return;
        }

        bool usePortable = DataDirectoryChoice.SelectedIndex == 1;
        DataDirectoryChoice.IsEnabled = false;
        ApplyDataDirectoryButton.IsEnabled = false;
        try
        {
            await location.SelectForNextLaunchAsync(usePortable);
            DataDirectoryStatus.Text = "保存先を設定しました。アプリを終了して起動し直すと反映されます。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DataDirectoryStatus.Text = $"保存先を変更できませんでした。{exception.Message}";
        }
        finally
        {
            DataDirectoryChoice.IsEnabled = true;
            UpdateDataDirectoryButton();
        }
    }
}
