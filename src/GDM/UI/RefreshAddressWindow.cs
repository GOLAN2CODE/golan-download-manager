using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

/// <summary>
/// IDM "Refresh download address": opens the page the file came from and waits for the user to click the
/// download there again; the browser's new download then lands on this item and it resumes.
/// </summary>
public sealed class RefreshAddressWindow : Window
{
    readonly DownloadItem _item;
    readonly string _page;
    readonly TextBlock _waiting;
    readonly DispatcherTimer _timer;
    bool _done;

    public RefreshAddressWindow(DownloadItem item)
    {
        _item = item;
        _page = AddressRefresh.SourcePage(item);
        Title = Loc.T("refresh.title");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true; // stays visible next to the browser while the user clicks
        Style = (Style)Application.Current.Resources["GdmWindow"];
        SetResourceReference(BackgroundProperty, "Surface");
        AutomationProperties.SetAutomationId(this, "RefreshAddressWindow");

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = Loc.T("refresh.heading"), FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

        var done = item.Downloaded > 0
            ? (item.TotalSize > 0 ? $"{Fmt.Size(item.Downloaded)} ({Fmt.Percent(item.Downloaded * 100.0 / item.TotalSize, "0")})" : Fmt.Size(item.Downloaded))
            : Loc.T("refresh.from_start");
        var body = new TextBlock
        {
            Text = _page.Length > 0 ? Loc.T("refresh.body", item.FileName, done) : Loc.T("refresh.no_page"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };
        AutomationProperties.SetAutomationId(body, "RefreshBodyText");
        root.Children.Add(body);

        _waiting = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap };
        _waiting.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        AutomationProperties.SetAutomationId(_waiting, "RefreshWaitingText");
        root.Children.Add(_waiting);

        var note = new TextBlock { Text = Loc.T("refresh.safe_note"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
        note.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        root.Children.Add(note);

        var row = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        if (_page.Length > 0)
        {
            var open = new Button { Content = Loc.T("refresh.open_page"), MinWidth = 120, Margin = new Thickness(0, 0, 8, 6) };
            open.SetResourceReference(StyleProperty, "Primary");
            AutomationProperties.SetAutomationId(open, "RefreshOpenPageBtn");
            open.Click += (_, _) => OpenSourcePage();
            row.Children.Add(open);
        }
        var paste = new Button { Content = Loc.T("refresh.paste"), MinWidth = 120, Margin = new Thickness(0, 0, 8, 6) };
        AutomationProperties.SetAutomationId(paste, "RefreshPasteBtn");
        paste.Click += (_, _) =>
        {
            App.Instance.AddressRefresh.Cancel(_item.Id);
            Close();
            MainWindow.AskNewAddress(App.Instance.MainView!, _item);
        };
        row.Children.Add(paste);
        var cancel = new Button { Content = Loc.T("refresh.cancel"), MinWidth = 90, IsCancel = true, Margin = new Thickness(0, 0, 8, 6) };
        AutomationProperties.SetAutomationId(cancel, "RefreshCancelBtn");
        cancel.Click += (_, _) => Close();
        row.Children.Add(cancel);
        root.Children.Add(row);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateWaiting();
        UpdateWaiting();
        _timer.Start();

        App.Instance.AddressRefreshed += OnRefreshed;
        App.Instance.AddressRefresh.Ended += OnEnded;
        Closed += (_, _) =>
        {
            _timer.Stop();
            App.Instance.AddressRefreshed -= OnRefreshed;
            App.Instance.AddressRefresh.Ended -= OnEnded;
            if (!_done) App.Instance.AddressRefresh.Cancel(_item.Id);
        };
    }

    public void OpenSourcePage()
    {
        if (_page.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(_page) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("refresh address: open page failed: " + ex.Message); }
    }

    void UpdateWaiting()
    {
        if (_done) return;
        var left = App.Instance.AddressRefresh.Remaining(DateTime.UtcNow);
        if (App.Instance.AddressRefresh.Pending(DateTime.UtcNow) != _item.Id || left <= TimeSpan.Zero)
        {
            _waiting.Text = Loc.T("refresh.timeout");
            _timer.Stop();
            return;
        }
        _waiting.Text = Loc.T("refresh.waiting", $"{(int)left.TotalMinutes}:{left.Seconds:00}");
    }

    void OnRefreshed(DownloadItem item)
    {
        if (item.Id != _item.Id) return;
        Dispatcher.BeginInvoke(() =>
        {
            _done = true;
            _timer.Stop();
            _waiting.Text = Loc.T("refresh.done_inline");
            var close = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            close.Tick += (_, _) => { close.Stop(); Close(); };
            close.Start();
        });
    }

    // Another item started a refresh (only one waits at a time): this window is no longer the one being waited for.
    void OnEnded(Guid id)
    {
        if (id != _item.Id || _done) return;
        Dispatcher.BeginInvoke(UpdateWaiting);
    }
}
