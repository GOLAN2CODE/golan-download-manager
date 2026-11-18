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
        SyncEnabledState();
        UpdateDisplay();
    }

    void SyncEnabledState()
    {
        bool on = EnabledBox.IsChecked == true;
        SpeedSlider.IsEnabled = on;
        SpeedBox.IsEnabled = on;
    }

    void EnabledChanged(object sender, RoutedEventArgs e) => SyncEnabledState();

    int CurrentLimitKB()
        => int.TryParse(SpeedBox.Text.Trim(), out var v)
            ? Math.Clamp(v, 16, 102400)
            : Math.Clamp((int)Math.Round(SpeedSlider.Value), 16, 102400);

    void SpeedBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || !int.TryParse(SpeedBox.Text.Trim(), out var value)) return;
        _updating = true;
        try
        {
            SpeedSlider.Value = Math.Clamp(value, 16, 102400);
        }
        finally
        {
            _updating = false;
        }
        UpdateDisplay();
    }

    void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            SpeedBox.Text = ((int)Math.Round(SpeedSlider.Value)).ToString();
        }
        finally
        {
            _updating = false;
        }
        UpdateDisplay();
    }

    void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var value))
        {
            EnabledBox.IsChecked = true;
            SyncEnabledState();
            SpeedSlider.Value = value;
        }
    }

    void UpdateDisplay() => SpeedText.Text = Fmt.Speed(CurrentLimitKB() * 1024L);

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.SpeedLimitKB = CurrentLimitKB();
        _settings.SpeedLimitEnabled = EnabledBox.IsChecked == true;
        _settings.SpeedLimitAlwaysOnStartup = StartupBox.IsChecked == true;
        _settings.Save();
        if (App.Instance != null)
        {
            App.Instance.SpeedRuleController?.SetManualOverride(_settings.SpeedLimitEnabled, _settings.SpeedLimitKB);
            App.Instance.Manager.ApplyLimiter();
            App.Instance.RefreshTrayMenu();
            App.Instance.MainView?.RefreshToolbarState();
        }
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = true;
        else
            Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = false;
        else
            Close();
    }
}
