using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class OptionsWindow
{
    StackPanel ConnectionPage()
    {
        var p = Page(Loc.T("win.options.connection.title"), Loc.T("win.options.connection.note"));
        var slider = new Slider
        {
            Minimum = 1,
            Maximum = 32,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Value = _working.MaxConnections
        };
        var value = new TextBlock
        {
            Text = _working.MaxConnections.ToString(),
            Foreground = Brush("AccentText"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0)
        };
        slider.ValueChanged += (_, _) =>
        {
            _working.MaxConnections = (int)slider.Value;
            value.Text = _working.MaxConnections.ToString();
        };
        p.Children.Add(Card(Label(Loc.T("win.options.connection.max_conn")), slider, value));
        var limits = new ListBox
        {
            MinHeight = 120,
            DisplayMemberPath = "Pattern"
        };
        limits.ItemsSource = _working.ServerLimits;
        var b = EditorButtons(
            () =>
            {
                var x = new ServerLimit { Pattern = "example.com" };
                if (EditServer(x))
                {
                    _working.ServerLimits.Add(x);
                }
                limits.Items.Refresh();
            },
            () =>
            {
                if (limits.SelectedItem is ServerLimit x && EditServer(x))
                {
                    limits.Items.Refresh();
                }
            },
            () =>
            {
                if (limits.SelectedItem is ServerLimit x)
                {
                    _working.ServerLimits.Remove(x);
                }
                limits.Items.Refresh();
            });
        p.Children.Add(Card(Label(Loc.T("win.options.connection.server_limits")), limits, b));
        var scheduleRulesBtn = new Button
        {
            Content = Loc.T($"main.menu.speedRules"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0)
        };
        scheduleRulesBtn.Click += (_, _) =>
        {
            new SpeedRulesWindow(_working, App.Instance?.SpeedRuleController) { Owner = this }.ShowDialog();
        };
        p.Children.Add(Card(
            Check(Loc.T("win.options.connection.quota_enable"), () => _working.QuotaEnabled, v => _working.QuotaEnabled = v),
            Label(Loc.T("win.options.connection.quota_mb")),
            Number(_working.QuotaMB, v => _working.QuotaMB = v),
            Label(Loc.T("win.options.connection.quota_hours")),
            Number(_working.QuotaHours, v => _working.QuotaHours = v, 1),
            Check(Loc.T("win.options.connection.quota_warn"), () => _working.QuotaWarn, v => _working.QuotaWarn = v),
            Check(Loc.T("win.options.connection.limiter_enable"), () => _working.SpeedLimitEnabled, v => _working.SpeedLimitEnabled = v),
            Label(Loc.T("win.options.connection.limiter_kb")),
            Number(_working.SpeedLimitKB, v => _working.SpeedLimitKB = v, 1),
            Check(Loc.T("win.options.connection.limiter_startup"), () => _working.SpeedLimitAlwaysOnStartup, v => _working.SpeedLimitAlwaysOnStartup = v),
            scheduleRulesBtn));
        return p;
    }

    bool EditServer(ServerLimit x)
    {
        var pattern = InputDialog.Ask(this, Loc.T("win.options.connection.server_dialog_title"), Loc.T("win.options.connection.server_pattern_prompt"), x.Pattern);
        if (pattern == null)
        {
            return false;
        }
        var max = InputDialog.Ask(this, Loc.T("win.options.connection.server_max_title"), Loc.T("win.options.connection.server_max_prompt"), x.MaxConnections.ToString());
        if (!int.TryParse(max, out var n) || n is < 1 or > 32)
        {
            return false;
        }
        x.Pattern = pattern;
        x.MaxConnections = n;
        return true;
    }

    StackPanel ProxyPage()
    {
        var p = Page(Loc.T("win.options.proxy.title"), Loc.T("win.options.proxy.note"));
        var proxyModes = new[]
        {
            ("none", Loc.T("win.options.proxy.mode.none")),
            ("system", Loc.T("win.options.proxy.mode.system")),
            ("manual", Loc.T("win.options.proxy.mode.manual"))
        };
        var proxyTypes = new[]
        {
            ("http", "HTTP"),
            ("https", "HTTPS"),
            ("socks5", "SOCKS5"),
            ("socks4", "SOCKS4")
        };
        var passBox = Password("proxy", _working.ProxyPassword);
        var clearPass = Check(Loc.T("win.options.proxy.clear_password"), () => false, v =>
        {
            if (v)
            {
                passBox.Password = "";
                _passwordEdits["proxy"] = "";
            }
            else if (passBox.Password.Length == 0)
            {
                _passwordEdits.Remove("proxy");
            }
        });
        passBox.PasswordChanged += (_, _) =>
        {
            if (passBox.Password.Length > 0 && clearPass.IsChecked == true)
            {
                clearPass.IsChecked = false;
            }
        };
        p.Children.Add(Card(
            Label(Loc.T("win.options.proxy.mode")),
            Combo(proxyModes, _working.ProxyMode, v => _working.ProxyMode = v),
            Label(Loc.T("win.options.proxy.type")),
            Combo(proxyTypes, _working.ProxyType, v => _working.ProxyType = v),
            Label(Loc.T("win.options.proxy.address")),
            Text(_working.ProxyHost, v => _working.ProxyHost = v, true),
            Label(Loc.T("win.options.proxy.port")),
            Number(_working.ProxyPort, v => _working.ProxyPort = v, 1, 65535),
            Label(Loc.T("win.options.proxy.user")),
            Text(_working.ProxyUser, v => _working.ProxyUser = v, true),
            Label(Loc.T("win.options.proxy.password")),
            passBox,
            clearPass,
            Label(Loc.T("win.options.proxy.bypass")),
            Text(_working.ProxyBypass, v => _working.ProxyBypass = v, true)));
        return p;
    }
}
