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

/// <summary>First-run guide: how to install the browser extension.</summary>
public sealed class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        Title = Loc.T("main.welcome.title");
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Style = (Style)Application.Current.Resources["GdmWindow"];

        var root = new StackPanel { Margin = new Thickness(26, 26, 26, 8) };
        var head = new DockPanel();
        head.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/gdm.png")), Width = 56, Height = 56 });
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = Loc.T("main.welcome.heading"), FontSize = 20, FontWeight = FontWeights.Bold });
        titles.Children.Add(new TextBlock { Text = Loc.T("main.welcome.subtitle"), Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap });
        // First run follows the Windows language; many Arabic speakers run an English Windows.
        // One click switches the language and reopens Golan, instead of hunting through Options.
        string otherLang = Loc.IsRtl ? "en" : "ar";
        var langBtn = new Button
        {
            Content = Loc.IsRtl ? "English" : "العربية",
            Padding = new Thickness(12, 4, 12, 4),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 0, 0, 0),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(langBtn, "WelcomeLanguageBtn");
        langBtn.Click += (_, _) =>
        {
            var settings = App.Instance.Settings;
            settings.Language = otherLang;
            settings.FirstRun = true; // show this welcome again, in the chosen language
            settings.Save();
            Program.RestartRequested = true;
            App.Instance.ExitApp();
        };
        DockPanel.SetDock(langBtn, Dock.Right);
        head.Children.Insert(0, langBtn);
        head.Children.Add(titles);
        root.Children.Add(head);

        var autoPanel = new ExtensionAutoInstallPanel(
            openManualGuide: exe => App.Instance.ShowBrowserGuide(this, exe))
        {
            Margin = new Thickness(0, 16, 0, 0),
        };
        root.Children.Add(autoPanel);

        // One clear action first (automatic install). The manual steps stay folded so the window fits
        // any screen and a first-time user is not faced with eight buttons at once.
        var card = new Border { Style = (Style)Application.Current.Resources["Card"], Margin = new Thickness(0, 14, 0, 0) };
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
        guideBtn.Click += (_, _) => App.Instance.ShowBrowserGuide(this);
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
        var manual = new Expander
        {
            Header = new TextBlock { Text = Loc.T("main.welcome.manualHeader"), FontWeight = FontWeights.SemiBold, FontSize = 14 },
            IsExpanded = false,
            Content = steps,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(manual, "WelcomeManualExpander");
        System.Windows.Automation.AutomationProperties.SetName(manual, Loc.T("main.welcome.manualHeader"));
        card.Child = manual;
        root.Children.Add(card);

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("main.welcome.tip"),
            Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
        });

        var done = new Button { Content = Loc.T("main.welcome.start"), Style = (Style)Application.Current.Resources["Primary"], HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 120, IsDefault = true, IsCancel = true };
        done.Click += (_, _) => Close();
        // The closing button is pinned under the scrolling part: it is always visible, whatever the screen height.
        var footer = new Border { Padding = new Thickness(26, 12, 26, 16), Child = done };
        DockPanel.SetDock(footer, Dock.Bottom);
        var layout = new DockPanel();
        layout.Children.Add(footer);
        layout.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        MaxHeight = Math.Max(480, SystemParameters.WorkArea.Height - 40);
        Content = layout;
    }
}
