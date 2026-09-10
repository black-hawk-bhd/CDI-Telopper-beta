using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EEWTelop.Wpf.Controls;

internal sealed class TrialMapPaletteDialog : Window
{
    public TrialMapPalette? Result { get; private set; }
    public TrialMapPaletteDialog(TrialMapPalette initial)
    {
        Title = "試験地図の配色"; Width = 540; Height = 710;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White; Foreground = Brushes.Black;
        var root = new DockPanel { Margin = new Thickness(14) };
        var fields = new Dictionary<string, TextBox>();
        var options = new StackPanel();
        options.Children.Add(new TextBlock { Text = "色を選択、または #RRGGBB を入力してください。\n色を変えても震度の数値・地域判定は変わりません。", Margin = new Thickness(0, 0, 0, 10) });
        var grid = new CheckBox { Content = "格子線を表示する", IsChecked = initial.ShowGrid, Margin = new Thickness(0, 8, 0, 8) };
        var presets = new StackPanel { Orientation = Orientation.Horizontal };
        void Apply(TrialMapPalette palette)
        {
            foreach (var pair in fields) pair.Value.Text = palette.Colors[pair.Key];
            grid.IsChecked = palette.ShowGrid;
        }
        foreach (string name in new[] { "ペーパー（初期配色）", "グラファイト" })
        {
            var button = new Button { Content = name, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8) };
            button.Click += (_, _) => Apply(name == "グラファイト" ? TrialMapPalette.Dark : TrialMapPalette.Default);
            presets.Children.Add(button);
        }
        options.Children.Add(presets); options.Children.Add(grid);
        DockPanel.SetDock(options, Dock.Top); root.Children.Add(options);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "保存して反映", Padding = new Thickness(12), Margin = new Thickness(8), IsDefault = true };
        save.Click += (_, _) =>
        {
            var invalid = fields.FirstOrDefault(p => !TrialMapPalette.IsValid(p.Value.Text.Trim()));
            if (invalid.Value is not null) { MessageBox.Show(this, "色は #RRGGBB 形式で指定してください。", "入力確認"); invalid.Value.Focus(); return; }
            var palette = new TrialMapPalette(fields.ToDictionary(p => p.Key, p => p.Value.Text.Trim()), grid.IsChecked == true);
            try { palette.Save(TrialMapPalette.SettingsPath); Result = palette; DialogResult = true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "配色を保存できませんでした。設定フォルダの書き込み権限を確認してください。", "保存エラー"); }
        };
        actions.Children.Add(save);
        actions.Children.Add(new Button { Content = "キャンセル", IsCancel = true, Padding = new Thickness(12), Margin = new Thickness(8) });
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var rows = new StackPanel();
        foreach (var field in TrialMapPalette.Fields)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock { Text = field.Label, Width = 200, VerticalAlignment = VerticalAlignment.Center });
            var input = new TextBox { Text = initial.Colors[field.Key], Width = 100, VerticalContentAlignment = VerticalAlignment.Center };
            fields.Add(field.Key, input); row.Children.Add(input);
            var picker = new Button { Content = "色を選択", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
            void Swatch()
            {
                if (!TrialMapPalette.IsValid(input.Text.Trim())) return;
                var color = (Color)ColorConverter.ConvertFromString(input.Text.Trim());
                picker.Background = new SolidColorBrush(color); picker.Foreground = TrialMapPalette.Contrast(color);
            }
            input.TextChanged += (_, _) => Swatch(); Swatch();
            picker.Click += (_, _) =>
            {
                using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
                if (TrialMapPalette.IsValid(input.Text.Trim())) dialog.Color = System.Drawing.ColorTranslator.FromHtml(input.Text.Trim());
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) input.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
            };
            row.Children.Add(picker); rows.Children.Add(row);
        }
        root.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
}
