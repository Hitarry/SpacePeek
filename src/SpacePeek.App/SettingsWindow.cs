using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpacePeek;

/// <summary>Minimal settings window: startup, trigger mode, cache.</summary>
internal sealed class SettingsWindow : Window
{
    private readonly Action _onChanged;
    private readonly CheckBox _startup;
    private readonly RadioButton _space;
    private readonly RadioButton _hotkey;

    public SettingsWindow(Settings settings, Action onChanged)
    {
        _onChanged = onChanged;

        Title = "快速预览 设置";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(250, 250, 252));
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        Icon = AppIcon.LoadWpfIcon();

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };

        panel.Children.Add(Header("启动"));
        _startup = new CheckBox
        {
            Content = "开机时自动启动 SpacePeek",
            IsChecked = Settings.IsStartupEnabled(),
            Margin = new Thickness(0, 4, 0, 12),
        };
        _startup.Checked += (_, _) => ToggleStartup(true);
        _startup.Unchecked += (_, _) => ToggleStartup(false);
        panel.Children.Add(_startup);

        panel.Children.Add(Header("触发方式"));
        _space = new RadioButton
        {
            Content = "单按空格键（在资源管理器里）",
            IsChecked = settings.TriggerMode == TriggerMode.Space,
            Margin = new Thickness(0, 4, 0, 4),
        };
        _hotkey = new RadioButton
        {
            Content = "Ctrl + Shift + Space",
            IsChecked = settings.TriggerMode == TriggerMode.Hotkey,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _space.Checked += (_, _) => ChangeMode(TriggerMode.Space);
        _hotkey.Checked += (_, _) => ChangeMode(TriggerMode.Hotkey);
        panel.Children.Add(_space);
        panel.Children.Add(_hotkey);

        panel.Children.Add(Header("缓存"));
        var clear = new Button
        {
            Content = "清空预览缓存",
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 4),
        };
        clear.Click += (_, _) =>
        {
            Settings.ClearCache();
            MessageBox.Show(this, "预览缓存已清空。", "快速预览", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        panel.Children.Add(clear);

        panel.Children.Add(new TextBlock
        {
            Text = "选中文件后按空格即可预览；再按一次关闭。",
            Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
            FontSize = 12,
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        Content = panel;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
        Margin = new Thickness(0, 10, 0, 2),
    };

    private void ToggleStartup(bool enabled)
    {
        if (!Settings.SetStartup(enabled))
        {
            MessageBox.Show(this, "无法修改开机启动项。", "快速预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _onChanged();
    }

    private void ChangeMode(TriggerMode mode) => _onChanged();
}

