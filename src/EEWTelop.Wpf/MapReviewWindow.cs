using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EEWTelop.Domain.Events;
using EEWTelop.Wpf.Controls;
using EEWTelop.Wpf.ViewModels;

namespace EEWTelop.Wpf;

internal sealed class MapReviewWindow : Window
{
    private readonly Func<IEnumerable<ReceivedTelegramViewModel>> _telegrams;
    private readonly ComboBox _selection = new() { DisplayMemberPath = nameof(ReceivedTelegramViewModel.DisplayText), MinWidth = 300, MaxWidth = 740, Margin = new Thickness(8) };
    private readonly ComboBox _extent = new() { ItemsSource = TrialQuakeMap.Extents.Select(x => x.Name).Prepend("自動").ToArray(), SelectedIndex = 0, Width = 170, Margin = new Thickness(8) };
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Foreground = Brushes.White, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
    private TrialMapPalette _palette;

    public MapReviewWindow(Func<IEnumerable<ReceivedTelegramViewModel>> telegrams, QuakeEvent? selected = null)
    {
        _telegrams = telegrams;
        _palette = TrialMapPalette.Load(TrialMapPalette.SettingsPath);
        Title = "CDI-Telopper 地図確認（選択電文・自動更新なし）";
        Width = 1120; Height = 850; MinWidth = 650; MinHeight = 480;
        Background = new SolidColorBrush(Color.FromRgb(40, 39, 42));
        var root = new DockPanel();
        var header = new StackPanel();
        var controls = new WrapPanel();
        controls.Children.Add(_selection);
        var reload = new Button { Content = "電文一覧を更新", Margin = new Thickness(8), Padding = new Thickness(10, 4, 10, 4) };
        reload.Click += (_, _) => Reload((_selection.SelectedItem as ReceivedTelegramViewModel)?.Event as QuakeEvent);
        controls.Children.Add(reload);
        header.Children.Add(controls);
        var toolbar = new WrapPanel();
        toolbar.Children.Add(_extent);
        var colors = new Button { Content = "配色を変更…", Margin = new Thickness(8), Padding = new Thickness(10, 4, 10, 4) };
        colors.Click += (_, _) =>
        {
            var dialog = new TrialMapPaletteDialog(_palette) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.Result is { } palette) { _palette = palette; Render(); }
        };
        toolbar.Children.Add(colors);
        var close = new Button { Content = "閉じる", Margin = new Thickness(8), Padding = new Thickness(10, 4, 10, 4) };
        close.Click += (_, _) => Close(); toolbar.Children.Add(close);
        header.Children.Add(toolbar); header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header); root.Children.Add(_image); Content = root;
        _selection.SelectionChanged += (_, _) => Render();
        _extent.SelectionChanged += (_, _) => Render();
        Reload(selected);
    }

    internal QuakeEvent? SelectedQuake => (_selection.SelectedItem as ReceivedTelegramViewModel)?.Event as QuakeEvent;

    internal void Reload(QuakeEvent? selected = null)
    {
        var items = _telegrams().Where(t => t.Event is QuakeEvent).ToList();
        // Keep a displayed snapshot even after the bounded reception history evicts it.
        if (selected is not null && !items.Any(t => ReferenceEquals(t.Event, selected)) &&
            _selection.SelectedItem is ReceivedTelegramViewModel previous && ReferenceEquals(previous.Event, selected))
            items.Insert(0, previous);
        _selection.ItemsSource = items;
        _selection.SelectedItem = items.FirstOrDefault(t => ReferenceEquals(t.Event, selected)) ?? items.FirstOrDefault();
        Render();
    }

    private void Render()
    {
        if (_selection.SelectedItem is not ReceivedTelegramViewModel item || item.Event is not QuakeEvent quake)
        {
            _image.Source = null;
            _status.Text = "確認できる地震電文がありません。受信・過去電文の取得後に「電文一覧を更新」を押してください。";
            return;
        }
        try
        {
            _image.Source = TrialQuakeMap.Render(quake, _extent.SelectedItem as string, _palette);
            _status.Text = $"{item.SourceText} / {quake.Provider} / {quake.IssuedAt.ToLocalTime():yyyy/MM/dd HH:mm:ss} 発表　選択電文の固定表示です。新着へ自動更新せず、字幕・音声の再実行もしません。";
        }
        catch (Exception)
        {
            _image.Source = null;
            _status.Text = "地図を描画できませんでした。受信・過去電文確認で本文を確認してください。";
        }
    }
}
