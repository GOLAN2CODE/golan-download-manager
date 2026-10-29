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
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)Application.Current.Resources["GdmWindow"];

        var root = new StackPanel { Margin = new Thickness(26) };
        var head = new DockPanel();
        head.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/gdm.png")), Width = 56, Height = 56 });
        var titles = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = Loc.T("main.welcome.heading"), FontSize = 20, FontWeight = FontWeights.Bold });
        titles.Children.Add(new TextBlock { Text = Loc.T("main.welcome.subtitle"), Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(titles);
        root.Children.Add(head);

        var card = new Border { Style = (Style)Application.Current.Resources["Card"], Margin = new Thickness(0, 20, 0, 0) };
        var steps = new StackPanel();
        steps.Children.Add(new TextBlock { Text = Loc.T("main.welcome.cardTitle"), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 10) });
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
        folderBtn.Click += (_, _) => BrowserIntegration.OpenExtensionFolder();
        browsers.Children.Add(folderBtn);
        steps.Children.Add(browsers);
        steps.Children.Add(new TextBox { Text = AppPaths.ExtensionDir, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 4, 0, 0) });
        steps.Children.Add(new TextBlock { Text = Loc.T("win.options.browsers.firefox_xpi"), Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 8, 0, 0) });
        steps.Children.Add(new TextBox { Text = BrowserIntegration.FirefoxXpiPath, IsReadOnly = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 4, 0, 0) });
        card.Child = steps;
        root.Children.Add(card);

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("main.welcome.tip"),
            Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
        });

        var done = new Button { Content = Loc.T("main.welcome.start"), Style = (Style)Application.Current.Resources["Primary"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0), MinWidth = 120, IsDefault = true };
        done.Click += (_, _) => Close();
        root.Children.Add(done);
        Content = root;
    }
}
