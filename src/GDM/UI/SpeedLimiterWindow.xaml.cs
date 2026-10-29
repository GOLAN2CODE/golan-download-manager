using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class SpeedLimiterWindow : Window
{
    readonly AppSettings _settings;
    bool _updating;

    public SpeedLimiterWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        SpeedBox.Text = Math.Clamp(_settings.SpeedLimitKB, 16, 102400).ToString();
        SpeedSlider.Value = Math.Clamp(_settings.SpeedLimitKB, 16, 102400);
        EnabledBox.IsChecked = _settings.SpeedLimitEnabled;
        StartupBox.IsChecked = _settings.SpeedLimitAlwaysOnStartup;
        UpdateDisplay();
    }

    void EnabledChanged(object sender, RoutedEventArgs e) { SpeedSlider.IsEnabled = EnabledBox.IsChecked == true; }
    void SpeedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !int.TryParse(SpeedBox.Text, out var value)) return;
        SpeedSlider.Value = Math.Clamp(value, 16, 102400);
        UpdateDisplay();
    }
    void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _updating = true;
        SpeedBox.Text = ((int)Math.Round(SpeedSlider.Value)).ToString();
        _updating = false;
        UpdateDisplay();
    }
    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var value)) SpeedSlider.Value = value;
    }
    void UpdateDisplay() => SpeedText.Text = Fmt.Speed((int)SpeedSlider.Value * 1024L);
    void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.SpeedLimitKB = Math.Clamp((int)Math.Round(SpeedSlider.Value), 16, 102400);
        _settings.SpeedLimitEnabled = EnabledBox.IsChecked == true;
        _settings.SpeedLimitAlwaysOnStartup = StartupBox.IsChecked == true;
        _settings.Save();
        App.Instance.Manager.ApplyLimiter();
        DialogResult = true;
    }
    void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
}
