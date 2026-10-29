using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;
using Golan.Gdm.Integration;
using Microsoft.Win32;

namespace Golan.Gdm.UI;

public partial class OptionsWindow : Window, INotifyPropertyChanged
{
    readonly DownloadManager _manager;
    readonly AppSettings _working;
    readonly Dictionary<string, StackPanel> _pages = [];
    readonly Dictionary<string, string> _passwordEdits = [];
    Action? _ensureVideoVersionLoaded;
    bool _valid = true;

    public bool IsValid
    {
        get => _valid;
        private set
        {
            _valid = value;
            PropertyChanged?.Invoke(this, new(nameof(IsValid)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    sealed record NavEntry(string Key, string Label, string Glyph);

    public OptionsWindow(DownloadManager manager, string? startTab = null)
    {
        if (Application.Current != null)
        {
            InitializeComponent();
        }
        else
        {
            NavList = new ListBox();
            NavList.SelectionChanged += NavList_SelectionChanged;
            PageHost = new ContentControl();
            PageScroll = new ScrollViewer { Content = PageHost };
            Content = PageScroll;
        }
        _manager = manager;
        _working = manager.Settings.Clone();
        DataContext = this;
        BuildNavigation();
        BuildPages();
        var selected = NavList.Items.Cast<NavEntry>().FirstOrDefault(x => x.Key == startTab) ?? (NavEntry)NavList.Items[0];
        NavList.SelectedItem = selected;
    }

    void BuildNavigation()
    {
        NavList.ItemsSource = new[]
        {
            new NavEntry("general", Loc.T("win.options.tab.general"), "\uE713"),
            new NavEntry("filetypes", Loc.T("win.options.tab.filetypes"), "\uE8A5"),
            new NavEntry("saveto", Loc.T("win.options.tab.saveto"), "\uE838"),
            new NavEntry("downloads", Loc.T("win.options.tab.downloads"), "\uE896"),
            new NavEntry("connection", Loc.T("win.options.tab.connection"), "\uEC4A"),
            new NavEntry("proxy", Loc.T("win.options.tab.proxy"), "\uE774"),
            new NavEntry("logins", Loc.T("win.options.tab.logins"), "\uE72E"),
            new NavEntry("sounds", Loc.T("win.options.tab.sounds"), "\uE767"),
            new NavEntry("panel", Loc.T("win.options.tab.panel"), "\uE714"),
            new NavEntry("browsers", Loc.T("win.options.tab.browsers"), "\uE774"),
            new NavEntry("video", Loc.T("win.options.tab.video"), "\uE714"),
            new NavEntry("torrent", Loc.T($"win.options.nav.torrent"), "\uE896"),
            new NavEntry("remote", Loc.T($"win.options.nav.remote"), "\uE8EA"),
            new NavEntry("updates", Loc.T($"win.options.nav.updates"), "\uE895")
        };
        var template = new DataTemplate();
        var panel = new FrameworkElementFactory(typeof(DockPanel));
        var glyph = new FrameworkElementFactory(typeof(TextBlock));
        glyph.SetValue(TextBlock.FontFamilyProperty, TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe UI"));
        glyph.SetValue(TextBlock.FontSizeProperty, 16d);
        glyph.SetValue(TextBlock.WidthProperty, 26d);
        glyph.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
        glyph.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        glyph.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        glyph.SetValue(TextBlock.FlowDirectionProperty, FlowDirection.LeftToRight);
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(NavEntry.Glyph)));
        panel.AppendChild(glyph);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(NavEntry.Label)));
        panel.AppendChild(label);
        template.VisualTree = panel;
        NavList.ItemTemplate = template;
    }

    void BuildPages()
    {
        _pages["general"] = GeneralPage();
        _pages["filetypes"] = FileTypesPage();
        _pages["saveto"] = SaveToPage();
        _pages["downloads"] = DownloadsPage();
        _pages["connection"] = ConnectionPage();
        _pages["proxy"] = ProxyPage();
        _pages["logins"] = LoginsPage();
        _pages["sounds"] = SoundsPage();
        _pages["panel"] = PanelPage();
        _pages["browsers"] = BrowsersPage();
        _pages["video"] = VideoPage();
        _pages["torrent"] = TorrentPage();
        _pages["remote"] = RemotePage();
        _pages["updates"] = UpdatesPage();
    }

    void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is NavEntry entry)
        {
            PageHost.Content = _pages[entry.Key];
            if (entry.Key == "video")
            {
                var load = Interlocked.Exchange(ref _ensureVideoVersionLoaded, null);
                load?.Invoke();
            }
        }
    }

    StackPanel Page(string title, string? note = null)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        if (note != null)
        {
            p.Children.Add(new TextBlock
            {
                Text = note,
                Foreground = Brush("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 18)
            });
        }
        return p;
    }

    Border Card(params UIElement[] children)
    {
        var panel = new StackPanel();
        string? lastLabel = null;
        foreach (var child in children)
        {
            if (child is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            {
                lastLabel = tb.Text;
            }
            else if (lastLabel != null)
            {
                if (child is Border { Child: DockPanel dp })
                {
                    foreach (UIElement sub in dp.Children)
                    {
                        if (sub is TextBox subTb)
                            AutomationProperties.SetName(subTb, lastLabel);
                    }
                }
                else
                {
                    AutomationProperties.SetName(child, lastLabel);
                }
                lastLabel = null;
            }
            panel.Children.Add(child);
        }
        return new Border
        {
            Style = TryFindResource("Card") as Style,
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel
        };
    }

    TextBlock Label(string text) => new() { Text = text, Style = TryFindResource("Label") as Style };

    SolidColorBrush Brush(string key) => TryFindResource(key) as SolidColorBrush ?? Brushes.Transparent;

    CheckBox Check(string text, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = get() };
        AutomationProperties.SetName(c, text);
        c.Checked += (_, _) => set(true);
        c.Unchecked += (_, _) => set(false);
        return c;
    }

    TextBox Text(string value, Action<string> set, bool ltr = false, int width = 0)
    {
        var t = new TextBox
        {
            Text = value
        };
        AutomationProperties.SetName(t, value);
        if (ltr)
        {
            t.FlowDirection = FlowDirection.LeftToRight;
        }
        if (width > 0)
        {
            t.Width = width;
            t.HorizontalAlignment = HorizontalAlignment.Left;
        }
        t.TextChanged += (_, _) => set(t.Text);
        return t;
    }

    TextBox Number(int value, Action<int> set, int min = 0, int max = int.MaxValue)
    {
        TextBox? t = null;
        t = Text(value.ToString(), s =>
        {
            if (int.TryParse(s, out var n) && n >= min && n <= max)
            {
                set(n);
                t!.ClearValue(TextBox.BorderBrushProperty);
                ValidateNumbers();
            }
            else
            {
                t!.BorderBrush = Brush("Danger");
                IsValid = false;
            }
        }, true, 110);
        t.LostFocus += (_, _) => ValidateNumbers();
        return t;
    }

    void ValidateNumbers()
    {
        IsValid = !FindVisualChildren<TextBox>(this).Any(x => x.BorderBrush == Brush("Danger"));
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result)
            {
                yield return result;
            }
            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    sealed record ComboChoice(string Key, string Label);

    ComboBox Combo((string Key, string Label)[] values, string selected, Action<string> set)
    {
        var items = values.Select(x => new ComboChoice(x.Key, x.Label)).ToArray();
        var c = new ComboBox
        {
            ItemsSource = items,
            DisplayMemberPath = nameof(ComboChoice.Label),
            SelectedValuePath = nameof(ComboChoice.Key),
            SelectedValue = selected
        };
        AutomationProperties.SetName(c, selected);
        c.SelectionChanged += (_, _) =>
        {
            if (c.SelectedValue is string v)
            {
                set(v);
            }
        };
        return c;
    }

    StackPanel EditorButtons(Action add, Action edit, Action delete)
    {
        var p = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        foreach (var (text, action) in new[] { (Loc.T("win.options.add"), add), (Loc.T("win.options.edit"), edit), (Loc.T("win.options.delete"), delete) })
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 6, 0)
            };
            AutomationProperties.SetName(b, text);
            b.Click += (_, _) => action();
            p.Children.Add(b);
        }
        return p;
    }

    Border PathBox(string value, Action<string> set, string? fileFilter = null)
    {
        var box = Text(value, set, true);
        var browse = new Button
        {
            Content = Loc.T("win.options.browse"),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(6, 0, 0, 0)
        };
        AutomationProperties.SetName(browse, Loc.T("win.options.browse"));
        browse.Click += (_, _) =>
        {
            if (fileFilter != null)
            {
                var d = new OpenFileDialog { Filter = fileFilter };
                if (d.ShowDialog(this) == true)
                {
                    box.Text = d.FileName;
                }
            }
            else
            {
                var f = new OpenFolderDialog();
                if (f.ShowDialog(this) == true)
                {
                    box.Text = f.FolderName;
                }
            }
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(browse, Dock.Left);
        row.Children.Add(browse);
        row.Children.Add(box);
        return new Border { Child = row };
    }

    PasswordBox Password(string key, string value)
    {
        var p = new PasswordBox
        {
            Password = "",
            Tag = Loc.T("win.options.password_keep_tag"),
            FlowDirection = FlowDirection.LeftToRight
        };
        AutomationProperties.SetName(p, key);
        p.PasswordChanged += (_, _) =>
        {
            if (p.Password.Length > 0)
            {
                _passwordEdits[key] = p.Password;
            }
            else
            {
                _passwordEdits.Remove(key);
            }
        };
        return p;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidateNumbers();
        if (!IsValid)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_working.DownloadRoot))
        {
            IsValid = false;
            return;
        }
        if (!Directory.Exists(_working.DownloadRoot))
        {
            try
            {
                Directory.CreateDirectory(_working.DownloadRoot);
            }
            catch (Exception ex)
            {
                Log.Write("options download root create failed: " + ex.Message);
                IsValid = false;
                return;
            }
        }
        if (_passwordEdits.TryGetValue("proxy", out var proxyPassword))
        {
            _working.ProxyPassword = proxyPassword;
        }
        if (!string.IsNullOrWhiteSpace(_working.VirusArgs) && _working.VirusArgs.Contains("[FILE]"))
        {
            _working.VirusArgs = _working.VirusArgs.Replace("\"[FILE]\"", "[FILE]").Replace("[FILE]", "\"[FILE]\"");
        }
        bool langChanged = !string.Equals(_manager.Settings.Language, _working.Language, StringComparison.OrdinalIgnoreCase);
        bool magnetChanged = _manager.Settings.RegisterMagnetProtocol != _working.RegisterMagnetProtocol;
        CopySettings(_manager.Settings, _working);
        if (langChanged)
        {
            Loc.Init(_manager.Settings.Language);
        }
        _manager.Settings.Save();
        _manager.ApplySettings();
        if (!AppPaths.IsPortable)
        {
            WindowsStartup.Apply(_manager.Settings.StartWithWindows);
            if (magnetChanged && !AppPaths.IsIsolated)
            {
                try { new MagnetProtocolRegistrar().ApplySetting(_manager.Settings.RegisterMagnetProtocol); }
                catch (Exception ex) { Log.Write("magnet protocol registration failed: " + ex.Message); }
            }
        }
        if (App.Instance != null)
        {
            if (App.Instance.PowerGuard != null)
            {
                App.Instance.PowerGuard.KeepSystemAwake = _manager.Settings.KeepSystemAwake;
                App.Instance.PowerGuard.KeepDisplayOn = _manager.Settings.KeepDisplayOn;
            }
            App.Instance.SpeedRuleController?.OnSettingsChanged();
            _ = RemoteWindow.EnsureServerStateAsync(_manager);
            App.Instance.MainView?.RefreshCategories();
            App.Instance.MainView?.UpdateSpeedGraphVisibility();
        }
        DialogResult = true;
    }

    static readonly HashSet<string> SkippedSettingsProps =
    [
        nameof(AppSettings.Queues),
        nameof(AppSettings.Columns),
        nameof(AppSettings.SortBy),
        nameof(AppSettings.SortDescending),
        nameof(AppSettings.HideCategories),
        nameof(AppSettings.SmallToolbar),
        nameof(AppSettings.DarkMode),
        nameof(AppSettings.ShowDropTarget),
        nameof(AppSettings.DropTargetLeft),
        nameof(AppSettings.DropTargetTop),
    ];

    public static void CopySettings(AppSettings target, AppSettings source)
    {
        var properties = typeof(AppSettings)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !SkippedSettingsProps.Contains(p.Name));
        foreach (var property in properties)
        {
            var value = property.GetValue(source);
            property.SetValue(target, value);
        }
        target.Normalize();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
