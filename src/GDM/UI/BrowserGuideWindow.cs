using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Golan.Gdm.Core;
using Golan.Gdm.Integration;

namespace Golan.Gdm.UI;

/// <summary>
/// Guided "add Golan to my browser": pick the browser and the profile, see live whether Developer mode is on, whether the
/// extension is loaded and whether it has connected to GDM. Chrome does not allow apps to install unpacked extensions, so
/// the guide opens the right page in the right profile, copies the folder path and checks every step for the user.
/// </summary>
public sealed class BrowserGuideWindow : Window
{
    readonly ComboBox _browsers = new() { MinWidth = 220 };
    readonly ListBox _profiles = new() { Height = 128 };
    readonly StackPanel _chromiumPanel = new();
    readonly StackPanel _firefoxPanel = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, FontSize = 14 };
    readonly TextBlock _devState = new() { FontWeight = FontWeights.SemiBold };
    readonly TextBlock _installState = new() { FontWeight = FontWeights.SemiBold };
    readonly TextBlock _toast = new() { Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 8, 0, 0) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DateTime _openedUtc = DateTime.UtcNow;
    readonly List<BrowserIntegration.BrowserInfo> _installed;

    public BrowserGuideWindow(string? preferExe = null)
    {
        Title = Loc.T("guide.title");
        Width = 660;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.Resources["GdmWindow"];
        AutomationProperties.SetAutomationId(this, "BrowserGuideWindow");

        _installed = BrowserIntegration.InstalledBrowsers().ToList();
        BrowserIntegration.ExtractExtension(); // the folder must exist and be current before the user picks it

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        var head = new DockPanel();
        head.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/gdm.png")), Width = 44, Height = 44 });
        var titles = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = Loc.T("guide.heading"), FontSize = 19, FontWeight = FontWeights.Bold });
        titles.Children.Add(new TextBlock { Text = Loc.T("guide.subtitle"), Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(titles);
        root.Children.Add(head);

        if (_installed.Count == 0)
        {
            root.Children.Add(new TextBlock { Text = Loc.T("guide.noBrowsers"), Margin = new Thickness(0, 18, 0, 0), TextWrapping = TextWrapping.Wrap });
            root.Children.Add(CloseButton());
            Content = root;
            return;
        }

        // Browser picker
        var browserRow = new DockPanel { Margin = new Thickness(0, 18, 0, 0) };
        browserRow.Children.Add(new TextBlock { Text = Loc.T("guide.browser"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontWeight = FontWeights.SemiBold });
        foreach (var b in _installed) _browsers.Items.Add(b.Name);
        AutomationProperties.SetAutomationId(_browsers, "GuideBrowserCombo");
        browserRow.Children.Add(_browsers);
        root.Children.Add(browserRow);

        // Chromium: profile + steps
        var profileCard = Card();
        var profilePanel = new StackPanel();
        profilePanel.Children.Add(new TextBlock { Text = Loc.T("guide.profile"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        AutomationProperties.SetAutomationId(_profiles, "GuideProfileList");
        profilePanel.Children.Add(_profiles);
        profileCard.Child = profilePanel;
        _chromiumPanel.Children.Add(profileCard);

        var stepsCard = Card();
        var steps = new StackPanel();
        AutomationProperties.SetAutomationId(_summary, "GuideSummary");
        steps.Children.Add(_summary);

        var open = new Button { Content = Loc.T("guide.openPage"), Style = (Style)Application.Current.Resources["Primary"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(open, "GuideOpenPageBtn");
        open.Click += (_, _) => OpenPage();
        steps.Children.Add(Step(1, Loc.T("guide.step1"), open));

        AutomationProperties.SetAutomationId(_devState, "GuideDevModeState");
        steps.Children.Add(Step(2, Loc.T("guide.step2"), _devState));

        var copy = new Button { Content = Loc.T("guide.copyPath"), Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetAutomationId(copy, "GuideCopyPathBtn");
        copy.Click += (_, _) => CopyText(AppPaths.ExtensionDir, Loc.T("guide.copied"));
        var folder = new Button { Content = Loc.T("guide.openFolder") };
        AutomationProperties.SetAutomationId(folder, "GuideOpenFolderBtn");
        folder.Click += (_, _) => { if (!BrowserIntegration.OpenExtensionFolder()) _toast.Text = Loc.T("guide.openFailed"); };
        var step3Buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Children = { copy, folder } };
        var pathBox = new TextBox { Text = AppPaths.ExtensionDir, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 6, 0, 0) };
        steps.Children.Add(Step(3, Loc.T("guide.step3"), new StackPanel { Children = { pathBox, step3Buttons } }));

        AutomationProperties.SetAutomationId(_installState, "GuideInstallState");
        steps.Children.Add(Step(4, Loc.T("guide.step4"), _installState));

        var fallback = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 10, 0, 0) };
        steps.Children.Add(fallback);
        var copyAddress = new Button { Content = Loc.T("guide.copyAddress"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(copyAddress, "GuideCopyAddressBtn");
        copyAddress.Click += (_, _) => { if (Selected is { } b) CopyText(b.ExtensionsPage, Loc.T("guide.copied")); };
        steps.Children.Add(copyAddress);
        steps.Children.Add(_toast);
        stepsCard.Child = steps;
        _chromiumPanel.Children.Add(stepsCard);
        root.Children.Add(_chromiumPanel);

        // Firefox
        var ffCard = Card();
        var ff = new StackPanel();
        ff.Children.Add(new TextBlock { Text = Loc.T("guide.ff.heading"), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        ff.Children.Add(new TextBlock { Text = Loc.T("guide.ff.note"), Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        var ffOpen = new Button { Content = Loc.T("guide.ff.open"), Style = (Style)Application.Current.Resources["Primary"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(ffOpen, "GuideFirefoxOpenBtn");
        ffOpen.Click += (_, _) => OpenPage();
        ff.Children.Add(Step(1, Loc.T("guide.ff.step1"), ffOpen));
        var ffCopy = new Button { Content = Loc.T("guide.copyPath"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(ffCopy, "GuideFirefoxCopyBtn");
        ffCopy.Click += (_, _) => CopyText(BrowserIntegration.FirefoxXpiPath, Loc.T("guide.copied"));
        ff.Children.Add(Step(2, Loc.T("guide.ff.step2"), new StackPanel
        {
            Children =
            {
                new TextBox { Text = BrowserIntegration.FirefoxXpiPath, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 6, 0, 0) },
                ffCopy,
            },
        }));
        ff.Children.Add(new TextBlock { Text = Loc.T("guide.ff.address"), Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        ffCard.Child = ff;
        _firefoxPanel.Children.Add(ffCard);
        root.Children.Add(_firefoxPanel);

        root.Children.Add(new TextBlock { Text = Loc.T("guide.honest"), Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), FontSize = 12 });
        root.Children.Add(CloseButton());
        // Small laptop screens: never taller than the work area; scroll instead of cutting the bottom off.
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 40);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        _browsers.SelectionChanged += (_, _) =>
        {
            if (Selected is not { } b) return;
            bool firefox = IsFirefox(b);
            _chromiumPanel.Visibility = firefox ? Visibility.Collapsed : Visibility.Visible;
            _firefoxPanel.Visibility = firefox ? Visibility.Visible : Visibility.Collapsed;
            fallback.Text = Loc.T("guide.fallback", b.ExtensionsPage);
            LoadProfiles(b);
        };
        _profiles.SelectionChanged += (_, _) => Refresh();
        int start = preferExe == null ? -1 : _installed.FindIndex(b => string.Equals(ExeName(b), preferExe, StringComparison.OrdinalIgnoreCase));
        if (start < 0) start = Math.Max(0, _installed.FindIndex(b => ExeName(b) == "chrome.exe"));
        _browsers.SelectedIndex = start;

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    BrowserIntegration.BrowserInfo? Selected => _browsers.SelectedIndex >= 0 && _browsers.SelectedIndex < _installed.Count ? _installed[_browsers.SelectedIndex] : null;

    BrowserProfile? SelectedProfile => (_profiles.SelectedItem as ListBoxItem)?.Tag as BrowserProfile;

    static string ExeName(BrowserIntegration.BrowserInfo b) => Path.GetFileName(b.ExePath ?? "").ToLowerInvariant();

    static bool IsFirefox(BrowserIntegration.BrowserInfo b) => ExeName(b) == "firefox.exe";

    void LoadProfiles(BrowserIntegration.BrowserInfo b)
    {
        _profiles.Items.Clear();
        if (IsFirefox(b)) return;
        var userData = BrowserProfiles.UserDataDir(ExeName(b));
        var profiles = userData == null ? [] : BrowserProfiles.ReadProfiles(userData);
        foreach (var p in profiles)
        {
            var item = new ListBoxItem { Content = p.Label, Tag = p, FlowDirection = FlowDirection.LeftToRight };
            AutomationProperties.SetName(item, p.Label);
            _profiles.Items.Add(item);
        }
        if (_profiles.Items.Count == 0)
            _profiles.Items.Add(new ListBoxItem { Content = Loc.T("guide.defaultProfile"), Tag = new BrowserProfile("", Loc.T("guide.defaultProfile"), "", true) });
        _profiles.SelectedIndex = 0;
        Refresh();
    }

    void Refresh()
    {
        if (Selected is not { } b || IsFirefox(b)) return;
        var exe = ExeName(b);
        var contact = ExtensionContact.Last(exe) ?? (ExtensionContact.Last() is { } any && any >= _openedUtc ? any : null);
        bool connectedRecently = contact is { } c && DateTime.UtcNow - c < TimeSpan.FromMinutes(10);

        ProfileExtensionState state = new(false, false, false);
        if (SelectedProfile is { } p && BrowserProfiles.UserDataDir(exe) is { } userData)
        {
            var profilePath = p.Directory.Length == 0 ? userData : Path.Combine(userData, p.Directory);
            state = BrowserProfiles.ReadState(profilePath, BrowserIntegration.ExtensionId, AppPaths.ExtensionDir);
        }

        SetState(_devState, state.DeveloperMode, Loc.T("guide.dev.on"), Loc.T("guide.dev.off"));
        if (state.Installed && state.Disabled) SetState(_installState, false, "", Loc.T("guide.ext.disabled"));
        else SetState(_installState, state.Installed, Loc.T("guide.ext.installed"), Loc.T("guide.ext.missing"));

        if (state.Installed && !state.Disabled && connectedRecently) Summary(Loc.T("guide.summary.done"), "Success");
        else if (state.Installed && state.Disabled) Summary(Loc.T("guide.summary.disabled"), "Warning");
        else if (state.Installed) Summary(Loc.T("guide.summary.installed"), "Success");
        else if (!state.DeveloperMode) Summary(Loc.T("guide.summary.devOff"), "Warning");
        else Summary(Loc.T("guide.summary.load"), "AccentText");
    }

    void Summary(string text, string brush)
    {
        _summary.Text = text;
        _summary.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    static void SetState(TextBlock t, bool ok, string okText, string badText)
    {
        t.Text = ok ? "✓ " + okText : "✗ " + badText;
        t.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Success" : "Danger");
    }

    void OpenPage()
    {
        if (Selected is not { } b) return;
        CopyText(IsFirefox(b) ? BrowserIntegration.FirefoxXpiPath : AppPaths.ExtensionDir, null);
        var profile = IsFirefox(b) ? null : SelectedProfile?.Directory;
        _toast.Text = BrowserProfiles.OpenExtensionsPage(b, profile) ? Loc.T("guide.opened") : Loc.T("guide.openFailed");
    }

    void CopyText(string text, string? confirmation)
    {
        try
        {
            App.Instance.MainView?.SuppressClipboard();
            Clipboard.SetText(text);
            if (confirmation != null) _toast.Text = confirmation;
        }
        catch (Exception ex)
        {
            Log.Write("guide clipboard failed: " + ex.Message);
            _toast.Text = Loc.T("guide.clipboardFailed");
        }
    }

    static Border Card() => new() { Style = (Style)Application.Current.Resources["Card"], Margin = new Thickness(0, 12, 0, 0) };

    static UIElement Step(int number, string text, UIElement extra)
    {
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var badge = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Ui.Res("AccentSoft"), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = number.ToString(), Foreground = Ui.Res("AccentText"), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        row.Children.Add(badge);
        var body = new StackPanel { Margin = new Thickness(10, 2, 0, 0) };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(extra);
        row.Children.Add(body);
        return row;
    }

    Button CloseButton()
    {
        var close = new Button { Content = Loc.T("guide.close"), MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0), IsCancel = true };
        AutomationProperties.SetAutomationId(close, "GuideCloseBtn");
        close.Click += (_, _) => Close();
        return close;
    }
}
