using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Licensing;

namespace Golan.Gdm.UI;

/// <summary>Shows the trial/licence status and this PC's machine code, and activates a pasted key.</summary>
public sealed class ActivationWindow : Window
{
    readonly TextBlock _status;
    readonly TextBox _keyBox;
    readonly TextBlock _result;

    public ActivationWindow()
    {
        Title = Loc.T("license.title");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.Resources["GdmWindow"];
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "ActivationWindow");

        var root = new StackPanel { Margin = new Thickness(26) };
        var head = new DockPanel();
        head.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/gdm.png")), Width = 48, Height = 48 });
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = Loc.T("license.heading"), FontSize = 19, FontWeight = FontWeights.Bold });
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontWeight = FontWeights.SemiBold };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_status, "LicenseStatusText");
        titles.Children.Add(_status);
        head.Children.Add(titles);
        root.Children.Add(head);

        // Machine code
        var machineCard = new Border { Style = (Style)Application.Current.Resources["Card"], Margin = new Thickness(0, 20, 0, 0) };
        var machinePanel = new StackPanel();
        machinePanel.Children.Add(new TextBlock { Text = Loc.T("license.machine.label"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var machineRow = new DockPanel();
        var copy = new Button { Content = Loc.T("license.copy"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(copy, "CopyMachineCodeBtn");
        DockPanel.SetDock(copy, Dock.Right);
        var code = MachineCode.Current;
        var machineBox = new TextBox
        {
            Text = code, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, FontFamily = new FontFamily("Consolas"),
            FontSize = 16, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(machineBox, "MachineCodeBox");
        copy.Click += (_, _) =>
        {
            try
            {
                App.Instance.MainView?.SuppressClipboard();
                Clipboard.SetText(code);
                copy.Content = Loc.T("license.copied");
            }
            catch (Exception ex) { Log.Write("copy machine code failed: " + ex.Message); }
        };
        machineRow.Children.Add(copy);
        machineRow.Children.Add(machineBox);
        machinePanel.Children.Add(machineRow);
        machinePanel.Children.Add(new TextBlock { Text = Loc.T("license.machine.hint"), Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        machineCard.Child = machinePanel;
        root.Children.Add(machineCard);

        // Activation key
        var keyCard = new Border { Style = (Style)Application.Current.Resources["Card"], Margin = new Thickness(0, 14, 0, 0) };
        var keyPanel = new StackPanel();
        keyPanel.Children.Add(new TextBlock { Text = Loc.T("license.key.label"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        _keyBox = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FlowDirection = FlowDirection.LeftToRight, FontFamily = new FontFamily("Consolas"),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_keyBox, "LicenseKeyBox");
        keyPanel.Children.Add(_keyBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var activate = new Button { Content = Loc.T("license.activate"), Style = (Style)Application.Current.Resources["Primary"], MinWidth = 110, IsDefault = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(activate, "ActivateBtn");
        activate.Click += (_, _) => ActivateKey();
        var paste = new Button { Content = Loc.T("license.paste"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(paste, "PasteKeyBtn");
        paste.Click += (_, _) =>
        {
            try { if (Clipboard.ContainsText()) _keyBox.Text = Clipboard.GetText(); }
            catch (Exception ex) { Log.Write("paste key failed: " + ex.Message); }
        };
        buttons.Children.Add(activate);
        buttons.Children.Add(paste);
        keyPanel.Children.Add(buttons);
        _result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_result, "ActivationResultText");
        keyPanel.Children.Add(_result);
        keyCard.Child = keyPanel;
        root.Children.Add(keyCard);

        var close = new Button { Content = Loc.T("license.close"), MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0), IsCancel = true };
        System.Windows.Automation.AutomationProperties.SetAutomationId(close, "CloseActivationBtn");
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = root;
        ShowStatus(LicenseGate.Status);
    }

    void ActivateKey()
    {
        var error = LicenseGate.Service.Activate(_keyBox.Text, out _);
        LicenseGate.Refresh();
        if (error == LicenseKeyError.None)
        {
            _result.Text = Loc.T("license.result.ok");
            _result.Foreground = Ui.Res("Success");
            App.Instance.OnLicenseChanged();
        }
        else
        {
            _result.Text = Loc.T(error switch
            {
                LicenseKeyError.Empty => "license.error.empty",
                LicenseKeyError.BadSignature => "license.error.signature",
                LicenseKeyError.WrongMachine => "license.error.machine",
                LicenseKeyError.Expired => "license.error.expired",
                LicenseKeyError.SaveFailed => "license.error.save",
                _ => "license.error.malformed",
            });
            _result.Foreground = Ui.Res("Danger");
        }
        ShowStatus(LicenseGate.Status);
    }

    void ShowStatus(LicenseStatus s)
    {
        _status.Text = Describe(s);
        _status.Foreground = Ui.Res(s.CanDownload ? (s.State == LicenseState.Trial && s.DaysLeft <= 5 ? "Warning" : "Success") : "Danger");
    }

    public static string Describe(LicenseStatus s) => s.State switch
    {
        LicenseState.Trial => Loc.T("license.status.trial", s.DaysLeft),
        LicenseState.Licensed when s.License is { IsLifetime: true } => Loc.T("license.status.lifetime", s.License.Name),
        LicenseState.Licensed => Loc.T("license.status.licensed", s.License?.Name ?? "", FormatDate(s.EndsUtc)),
        LicenseState.LicenseExpired => Loc.T("license.status.licenseExpired", FormatDate(s.EndsUtc)),
        LicenseState.ClockTampered => Loc.T("license.status.clock"),
        _ => Loc.T("license.status.trialExpired"),
    };

    static string FormatDate(DateTime? utc) => utc?.ToLocalTime().ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) ?? "";
}
