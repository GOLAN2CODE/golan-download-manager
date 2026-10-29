using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using Golan.Gdm.Core;
using Golan.Gdm.Integration.Remote;

namespace Golan.Gdm.UI;

public partial class RemoteWindow : Window
{
    static RemoteServer? _activeServer;
    static RemoteAuthManager? _activeAuth;

    readonly DownloadManager _manager;
    readonly DispatcherTimer _uiTimer;
    readonly ObservableCollection<DeviceRow> _deviceRows = [];

    public sealed record DeviceRow(string Id, string Name, string CreatedAtText, string LastSeenAtText, DateTimeOffset LastSeenAt);

    static string V5T(string key, params object?[] args)
        => args.Length == 0 ? Loc.T(key) : Loc.T(key, args);

    public RemoteWindow(DownloadManager manager)
    {
        InitializeComponent();
        FlowDirection = Loc.FlowDirection;
        _manager = manager;

        EnsureAuth(_manager.Settings);

        var interfaces = RemoteServer.GetLocalIpv4Interfaces().Select(ip => ip.ToString()).ToList();
        if (!interfaces.Contains(_manager.Settings.RemoteBindAddress)
            && IPAddress.TryParse(_manager.Settings.RemoteBindAddress, out var savedIp)
            && RemoteServer.IsAllowedBindAddress(savedIp))
        {
            interfaces.Add(savedIp.ToString());
        }

        InterfaceCombo.ItemsSource = interfaces;
        InterfaceCombo.SelectedItem = interfaces.Contains(_manager.Settings.RemoteBindAddress)
            ? _manager.Settings.RemoteBindAddress
            : interfaces.FirstOrDefault() ?? "127.0.0.1";
        InterfaceCombo.SelectionChanged += (_, _) => RefreshQrAndStatus();

        EnableCheck.IsChecked = _manager.Settings.RemoteEnabled;
        PortBox.Text = _manager.Settings.RemotePort.ToString();
        DevicesGrid.ItemsSource = _deviceRows;

        CertFpTitleText.Text = V5T("remote.tls.title");
        CertHintText.Text = V5T("remote.tls.hint");
        CopyFpBtn.Content = V5T("remote.tls.copyFp");
        RegenerateCertBtn.Content = V5T("remote.tls.regenerateCert");

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => RefreshLiveState();
        _uiTimer.Start();
        Closed += (_, _) => _uiTimer.Stop();

        RefreshQrAndStatus();
        RefreshLiveState();
    }

    static RemoteAuthManager EnsureAuth(AppSettings settings)
    {
        _activeAuth ??= new RemoteAuthManager(settings.RemoteDevices, () => settings.Save());
        return _activeAuth;
    }

    public static async Task EnsureServerStateAsync(DownloadManager manager)
    {
        var settings = manager.Settings;
        var auth = EnsureAuth(settings);

        if (_activeServer != null)
        {
            await _activeServer.StopAsync();
            _activeServer = null;
        }

        if (!settings.RemoteEnabled)
            return;

        if (!IPAddress.TryParse(settings.RemoteBindAddress, out var bindIp) || !RemoteServer.IsAllowedBindAddress(bindIp))
            bindIp = IPAddress.Loopback;

        int port = settings.RemotePort is >= 1024 and <= 65535 ? settings.RemotePort : 8765;
        var target = new DownloadManagerRemoteTarget(manager);
        var srv = new RemoteServer(target, auth, bindIp, port);
        await srv.StartAsync();
        _activeServer = srv;
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selectedIp = InterfaceCombo.SelectedItem as string ?? "127.0.0.1";
        if (!IPAddress.TryParse(selectedIp, out var ip) || !RemoteServer.IsAllowedBindAddress(ip))
        {
            StatusText.Text = V5T("remote.status.error", V5T("remote.err.invalid_ip"));
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1024 or > 65535)
        {
            StatusText.Text = V5T("remote.status.error", V5T("remote.err.invalid_port"));
            return;
        }

        _manager.Settings.RemoteEnabled = EnableCheck.IsChecked == true;
        _manager.Settings.RemoteBindAddress = ip.ToString();
        _manager.Settings.RemotePort = port;
        _manager.Settings.Save();

        ApplyBtn.IsEnabled = false;
        try
        {
            await EnsureServerStateAsync(_manager);
            RefreshQrAndStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = V5T("remote.status.error", ex.Message);
        }
        finally
        {
            ApplyBtn.IsEnabled = true;
        }
    }

    void CopyFp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(CertFpBox.Text))
                Clipboard.SetText(CertFpBox.Text);
        }
        catch { }
    }

    async void RegenerateCert_Click(object sender, RoutedEventArgs e)
    {
        string ipStr = InterfaceCombo.SelectedItem as string ?? _manager.Settings.RemoteBindAddress;
        if (!IPAddress.TryParse(ipStr, out var ipAddr) || !RemoteServer.IsAllowedBindAddress(ipAddr))
            ipAddr = IPAddress.Loopback;

        RegenerateCertBtn.IsEnabled = false;
        try
        {
            var additionalIps = ipAddr.Equals(IPAddress.Loopback) ? null : RemoteServer.GetLocalIpv4Interfaces();
            using var _ = RemoteTls.RegenerateCertificate(ipAddr, additionalIps);
            if (_manager.Settings.RemoteEnabled)
                await EnsureServerStateAsync(_manager);
            RefreshQrAndStatus();
            StatusText.Text = V5T("remote.tls.regenerated");
        }
        catch (Exception ex)
        {
            StatusText.Text = V5T("remote.status.error", ex.Message);
        }
        finally
        {
            RegenerateCertBtn.IsEnabled = true;
        }
    }

    void GeneratePin_Click(object sender, RoutedEventArgs e)
    {
        var auth = EnsureAuth(_manager.Settings);
        var pin = auth.GeneratePin();
        PinText.Text = pin;
        RefreshLiveState();
    }

    void RevokeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesGrid.SelectedItem is not DeviceRow row) return;
        var auth = EnsureAuth(_manager.Settings);
        auth.RevokeDevice(row.Id);
        RefreshLiveState();
    }

    void RevokeAll_Click(object sender, RoutedEventArgs e)
    {
        var auth = EnsureAuth(_manager.Settings);
        auth.RevokeAllDevices();
        RefreshLiveState();
    }

    void RefreshQrAndStatus()
    {
        string ip = InterfaceCombo.SelectedItem as string ?? _manager.Settings.RemoteBindAddress;
        if (!IPAddress.TryParse(ip, out var ipAddr) || !RemoteServer.IsAllowedBindAddress(ipAddr))
            ipAddr = IPAddress.Loopback;

        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1 or > 65535)
            port = _manager.Settings.RemotePort;

        string fpGroups;
        string fpColon;
        string qrUrl;

        if (_activeServer is { IsRunning: true } && _activeServer.BindAddress.Equals(ipAddr) && _activeServer.Port == port)
        {
            fpGroups = _activeServer.CertificateFingerprintGroups;
            fpColon = _activeServer.CertificateFingerprintColon;
            qrUrl = _activeServer.QrPayloadUri;
        }
        else
        {
            var additionalIps = ipAddr.Equals(IPAddress.Loopback) ? null : RemoteServer.GetLocalIpv4Interfaces();
            using var cert = RemoteTls.EnsureCertificate(ipAddr, additionalIps);
            fpGroups = RemoteTls.FormatFingerprintShortGroups(cert);
            fpColon = RemoteTls.FormatFingerprintColonBytes(cert);
            qrUrl = RemoteTls.BuildPairingUrl(new Uri($"https://{ipAddr}:{port}/"), cert);
        }

        UrlBox.Text = qrUrl;
        CertFpBox.Text = fpGroups;
        CertFpBox.ToolTip = fpColon;

        try
        {
            QrImage.Source = QrCode.RenderWpfBitmap(qrUrl, moduleScale: 6, quietZone: 4);
        }
        catch { }

        if (_activeServer is { IsRunning: true })
            StatusText.Text = V5T("remote.status.running", _activeServer.BaseUri.AbsoluteUri);
        else
            StatusText.Text = V5T("remote.status.stopped");
    }

    void RefreshLiveState()
    {
        var auth = EnsureAuth(_manager.Settings);
        var activePin = auth.ActivePin;
        var expiresAt = auth.PinExpiresAt;

        if (activePin != null && expiresAt != null)
        {
            PinText.Text = activePin;
            var rem = expiresAt.Value - DateTimeOffset.UtcNow;
            if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
            PinExpiryText.Text = V5T("remote.pair.pin_expires", (int)rem.TotalMinutes, rem.Seconds);
        }
        else
        {
            if (PinText.Text != V5T("remote.pair.pin_none"))
                PinExpiryText.Text = V5T("remote.pair.pin_expired");
            else
                PinExpiryText.Text = "";
            PinText.Text = V5T("remote.pair.pin_none");
        }

        var snapshot = auth.GetDevicesSnapshot();
        if (!snapshot.Select(d => (d.Id, d.LastSeenAt)).SequenceEqual(_deviceRows.Select(r => (r.Id, r.LastSeenAt))))
        {
            var selectedId = (DevicesGrid.SelectedItem as DeviceRow)?.Id;
            _deviceRows.Clear();
            foreach (var d in snapshot)
            {
                _deviceRows.Add(new DeviceRow(
                    d.Id,
                    d.Name,
                    d.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    d.LastSeenAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    d.LastSeenAt));
            }
            if (selectedId != null)
                DevicesGrid.SelectedItem = _deviceRows.FirstOrDefault(r => r.Id == selectedId);
        }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
