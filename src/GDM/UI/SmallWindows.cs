using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Golan.Gdm.Core;
using Golan.Gdm.Integration;

namespace Golan.Gdm.UI;

static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static Border Badge(string glyph, Brush bg, Brush fg, double size = 48) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 4), Background = bg,
        Child = new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = size * 0.45,
            Foreground = fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            FlowDirection = FlowDirection.LeftToRight,
        },
    };

    public static Window Shell(string title, double width)
    {
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Style = (Style)Application.Current.Resources["GdmWindow"],
        };
    }
}

/// <summary>First-run single-window wizard: Step 1 = Welcome + language switch, Step 2 = Extension offer with profile selection.</summary>
public sealed class WelcomeWindow : Window
{
    readonly AppSettings _settings;
    readonly StackPanel _step1Panel;
    readonly StackPanel _step2Panel;
    readonly TextBlock _stepBadge;
    readonly Button _nextBtn;
    readonly Button _skipBtn;
    readonly Button _backBtn;
    readonly Button _doneBtn;
    readonly Expander _manualExpander;
    bool _installClicked;
    bool _switchingLanguage;

    public int CurrentStep { get; private set; } = 1;
    public ExtensionAutoInstallPanel AutoInstallPanel { get; }

    public WelcomeWindow() : this(null, null) { }

    public WelcomeWindow(AppSettings? settings, IReadOnlyList<BrowserIntegration.BrowserInfo>? detectedBrowsers = null)
    {
        _settings = settings ?? App.Instance?.Settings ?? new AppSettings();
        Title = Loc.T("main.welcome.title");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Style = (Style)Application.Current.Resources["GdmWindow"];
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, "WelcomeWindow");

        var root = new StackPanel { Margin = new Thickness(26, 24, 26, 10) };

        // Shared header with app icon, title/subtitle, step indicator, and one-click language switch.
        var head = new DockPanel();
        string otherLang = Loc.IsRtl ? "en" : "ar";
        var langLabel = Loc.T("main.welcome.switchLang");
        var langBtn = new Button
        {
            Content = langLabel,
            Padding = new Thickness(12, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(langBtn, "WelcomeLanguageBtn");
        System.Windows.Automation.AutomationProperties.SetName(langBtn, langLabel);
        langBtn.Click += (_, _) =>
        {
            _switchingLanguage = true;
            var s = _settings ?? App.Instance?.Settings;
            if (s != null)
            {
                s.Language = otherLang;
                s.FirstRun = true; // show this welcome again, in the chosen language
                s.Save();
            }
            Program.RestartRequested = true;
            App.Instance?.ExitApp();
        };
        DockPanel.SetDock(langBtn, Dock.Right);
        head.Children.Add(langBtn);

        try
        {
            head.Children.Add(new Image
            {
                Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/gdm.png")),
                Width = 54,
                Height = 54,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }
        catch { /* pack URI unavailable in headless tests */ }

        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _stepBadge = new TextBlock
        {
            Text = Loc.T("main.welcome.step_indicator", 1, 2),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        };
        _stepBadge.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        titles.Children.Add(_stepBadge);
        titles.Children.Add(new TextBlock { Text = Loc.T("main.welcome.heading"), FontSize = 20, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        titles.Children.Add(new TextBlock { Text = Loc.T("main.welcome.subtitle"), Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        head.Children.Add(titles);
        root.Children.Add(head);

        // -------- Step 1: Welcome overview --------
        _step1Panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_step1Panel, "WelcomeStep1Panel");

        var introCard = new Border { Style = (Style)Application.Current.Resources["Card"] };
        var introStack = new StackPanel();
        string[] highlights =
        [
            Loc.T("main.welcome.highlight1"),
            Loc.T("main.welcome.highlight2"),
            Loc.T("main.welcome.highlight3"),
        ];
        foreach (var hl in highlights)
        {
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5) };
            var iconBadge = Ui.Badge("\uE73E", Ui.Res("AccentSoft"), Ui.Res("AccentText"), 26);
            iconBadge.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(iconBadge);
            row.Children.Add(new TextBlock
            {
                Text = hl,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            });
            introStack.Children.Add(row);
        }
        introCard.Child = introStack;
        _step1Panel.Children.Add(introCard);

        _step1Panel.Children.Add(new TextBlock
        {
            Text = Loc.T("main.welcome.tip"),
            Foreground = Ui.Res("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
        });
        root.Children.Add(_step1Panel);

        // -------- Step 2: Extension offer + profile chooser (never auto-installs without user click) --------
        _step2Panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_step2Panel, "WelcomeStep2Panel");

        var browsersForOffer = detectedBrowsers ?? ExtensionAutoInstaller.InstalledSupportedBrowsers();
        if (browsersForOffer.Count > 0)
        {
            var names = Loc.IsRtl
                ? string.Join("‏، ", browsersForOffer.Select(b => b.Name)) + "‏"
                : string.Join(", ", browsersForOffer.Select(b => b.Name));
            var offerIntro = new TextBlock
            {
                Text = Loc.T("autoinstall.offer.body", names),
                Foreground = Ui.Res("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            };
            _step2Panel.Children.Add(offerIntro);
        }

        _manualExpander = new Expander
        {
            Header = new TextBlock { Text = Loc.T("main.welcome.manualHeader"), FontWeight = FontWeights.SemiBold, FontSize = 14 },
            IsExpanded = false,
        };

        AutoInstallPanel = new ExtensionAutoInstallPanel(
            targetBrowsers: detectedBrowsers,
            openManualGuide: _ => _manualExpander.IsExpanded = true,
            onCompleted: _ =>
            {
                _installClicked = true;
                _doneBtn!.Content = Loc.T("main.welcome.start");
                System.Windows.Automation.AutomationProperties.SetName(_doneBtn, Loc.T("main.welcome.start"));
            },
            inlineManualGuide: true);
        AutoInstallPanel.InstallButton.Click += (_, _) => _installClicked = true;
        _step2Panel.Children.Add(AutoInstallPanel);

        var card = new Border { Style = (Style)Application.Current.Resources["Card"], Margin = new Thickness(0, 12, 0, 0) };
        var steps = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        string[] lines =
        [
            Loc.T("main.welcome.step1"),
            Loc.T("main.welcome.step2"),
            Loc.T("main.welcome.step3"),
            Loc.T("main.welcome.step4"),
        ];
        for (int i = 0; i < lines.Length; i++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.Res("AccentSoft"), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = (i + 1).ToString(), Foreground = Ui.Res("AccentText"), FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            row.Children.Add(new TextBlock { Text = lines[i], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 2, 0, 0) });
            steps.Children.Add(row);
        }

        var guideBtn = new Button { Content = Loc.T("guide.welcomeButton"), Style = (Style)Application.Current.Resources["Primary"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(guideBtn, "WelcomeGuideBtn");
        guideBtn.Click += (_, _) => App.Instance?.ShowBrowserGuide(this);
        steps.Children.Add(guideBtn);

        var browsers = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var b in BrowserIntegration.InstalledBrowsers())
        {
            var btn = new Button { Content = Loc.T("main.welcome.openExt", b.Name), Margin = new Thickness(0, 0, 8, 8) };
            btn.Click += (_, _) =>
            {
                try { BrowserIntegration.OpenExtensionsPage(b); }
                catch (Exception ex) { Log.Write("welcome open extensions page failed: " + ex.Message); }
            };
            browsers.Children.Add(btn);
        }
        var folderBtn = new Button { Content = Loc.T("main.welcome.openFolder"), Margin = new Thickness(0, 0, 8, 8) };
        folderBtn.Click += (_, _) =>
        {
            try { BrowserIntegration.OpenExtensionFolder(); }
            catch (Exception ex) { Log.Write("welcome open extension folder failed: " + ex.Message); }
        };
        browsers.Children.Add(folderBtn);
        steps.Children.Add(browsers);

        var extDirBox = new TextBox { Text = AppPaths.ExtensionDir, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 4, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(extDirBox, Loc.T("main.welcome.openFolder"));
        steps.Children.Add(extDirBox);
        steps.Children.Add(new TextBlock { Text = Loc.T("win.options.browsers.firefox_xpi"), Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 8, 0, 0) });
        var xpiBox = new TextBox { Text = BrowserIntegration.FirefoxXpiPath, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 4, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(xpiBox, Loc.T("win.options.browsers.firefox_xpi"));
        steps.Children.Add(xpiBox);

        _manualExpander.Content = steps;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_manualExpander, "WelcomeManualExpander");
        System.Windows.Automation.AutomationProperties.SetName(_manualExpander, Loc.T("main.welcome.manualHeader"));
        card.Child = _manualExpander;
        _step2Panel.Children.Add(card);
        root.Children.Add(_step2Panel);

        // -------- Footer buttons --------
        var footerBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };

        _nextBtn = new Button
        {
            Content = Loc.T("main.welcome.next"),
            Style = (Style)Application.Current.Resources["Primary"],
            MinWidth = 120,
            IsDefault = true,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_nextBtn, "WelcomeNextBtn");
        _nextBtn.Click += (_, _) => ShowStep(2);

        _skipBtn = new Button
        {
            Content = Loc.T("autoinstall.offer.later"),
            MinWidth = 100,
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_skipBtn, "WelcomeSkipBtn");
        _skipBtn.Click += (_, _) =>
        {
            ExtensionOfferPolicy.RecordChoice(_settings, installNow: false, save: true);
            Close();
        };

        _backBtn = new Button
        {
            Content = Loc.T("win.grabber.back"),
            MinWidth = 96,
            Visibility = Visibility.Collapsed,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_backBtn, "WelcomeBackBtn");
        _backBtn.Click += (_, _) => ShowStep(1);

        _doneBtn = new Button
        {
            Content = Loc.T("main.welcome.start"),
            Style = (Style)Application.Current.Resources["Primary"],
            MinWidth = 130,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_doneBtn, "WelcomeDoneBtn");
        _doneBtn.Click += (_, _) =>
        {
            ExtensionOfferPolicy.RecordChoice(_settings, installNow: _installClicked, save: true);
            Close();
        };

        footerBar.Children.Add(_nextBtn);
        footerBar.Children.Add(_skipBtn);
        footerBar.Children.Add(_backBtn);
        footerBar.Children.Add(_doneBtn);

        Closed += (_, _) =>
        {
            if (!_switchingLanguage && _settings.FirstRun)
                ExtensionOfferPolicy.RecordChoice(_settings, installNow: _installClicked, save: true);
        };

        var footer = new Border { Padding = new Thickness(26, 12, 26, 16), Child = footerBar };
        DockPanel.SetDock(footer, Dock.Bottom);
        var layout = new DockPanel();
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 40);
        Content = layout;
    }

    public void ShowStep(int step)
    {
        CurrentStep = step == 2 ? 2 : 1;
        bool isStep2 = CurrentStep == 2;
        _stepBadge.Text = Loc.T("main.welcome.step_indicator", CurrentStep, 2);
        _step1Panel.Visibility = isStep2 ? Visibility.Collapsed : Visibility.Visible;
        _step2Panel.Visibility = isStep2 ? Visibility.Visible : Visibility.Collapsed;

        _nextBtn.Visibility = isStep2 ? Visibility.Collapsed : Visibility.Visible;
        _nextBtn.IsDefault = !isStep2;
        _skipBtn.Visibility = isStep2 ? Visibility.Collapsed : Visibility.Visible;
        _skipBtn.IsCancel = !isStep2;

        _backBtn.Visibility = isStep2 ? Visibility.Visible : Visibility.Collapsed;
        _doneBtn.Visibility = isStep2 ? Visibility.Visible : Visibility.Collapsed;
        _doneBtn.IsDefault = isStep2;
        _doneBtn.IsCancel = isStep2;

        if (isStep2)
        {
            ExtensionOfferPolicy.MarkShown(_settings, save: false);
            _ = AutoInstallPanel.EnsureTargetsAsync();
        }
    }
}
