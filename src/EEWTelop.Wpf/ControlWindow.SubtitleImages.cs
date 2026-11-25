using System.IO;
using System.Windows;
using System.Windows.Controls;
using EEWTelop.Wpf.Obs;
using EEWTelop.Wpf.ViewModels;
using Microsoft.Win32;

namespace EEWTelop.Wpf;

public partial class ControlWindow
{
    private void OnSelectSubtitleImage(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SubtitleImageEditor editor }) return;

        var dialog = new OpenFileDialog
        {
            Title = "字幕と一緒に送出するPNGを選択",
            Filter = "PNG画像 (*.png)|*.png",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            editor.SetImage(Path.GetFileName(dialog.FileName), SubtitlePng.ReadFile(dialog.FileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, exception.Message, "PNG画像を登録できませんでした", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnRemoveSubtitleImage(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SubtitleImageEditor editor }) editor.ClearImage();
    }
}
