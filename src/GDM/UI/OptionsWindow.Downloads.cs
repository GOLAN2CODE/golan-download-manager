using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class OptionsWindow
{
    StackPanel DownloadsPage()
    {
        var p = Page(Loc.T("win.options.downloads.title"), Loc.T("win.options.downloads.note"));
        var dupChoices = new[]
        {
            ("ask", Loc.T("win.options.downloads.dup.ask")),
            ("number", Loc.T("win.options.downloads.dup.number")),
            ("overwrite", Loc.T("win.options.downloads.dup.overwrite")),
            ("resume", Loc.T("win.options.downloads.dup.resume"))
        };
        p.Children.Add(Card(
            Check(Loc.T("win.options.downloads.show_add_browser"), () => _working.ShowAddDialogFromBrowser, v => _working.ShowAddDialogFromBrowser = v),
            Check(Loc.T("win.options.downloads.start_while_dialog"), () => _working.StartImmediatelyWhileDialog, v => _working.StartImmediatelyWhileDialog = v),
            Check(Loc.T("win.options.downloads.show_progress"), () => _working.ShowProgressWindow, v => _working.ShowProgressWindow = v),
            Check(Loc.T("win.options.downloads.show_complete"), () => _working.ShowCompleteDialog, v => _working.ShowCompleteDialog = v),
            Check(Loc.T("win.options.downloads.show_queue_later"), () => _working.ShowQueuePanelOnLater, v => _working.ShowQueuePanelOnLater = v),
            Check(Loc.T("win.options.downloads.confirm_delete"), () => _working.ConfirmDelete, v => _working.ConfirmDelete = v),
            Label(Loc.T("win.options.downloads.dup_label")),
            Combo(dupChoices, _working.DuplicateAction, v => _working.DuplicateAction = v),
            Label(Loc.T("win.options.downloads.manual_ua")),
            Text(_working.ManualUserAgent, v => _working.ManualUserAgent = v, true)));

        var targetChoices = new[]
        {
            (nameof(ExtractTargetMode.Subfolder), Loc.T($"extract.target.subfolder")),
            (nameof(ExtractTargetMode.SameFolder), Loc.T($"extract.target.same_folder"))
        };
        var ruleChoices = new[]
        {
            (nameof(ExistingFileRule.Rename), Loc.T($"extract.rule.rename")),
            (nameof(ExistingFileRule.Skip), Loc.T($"extract.rule.skip")),
            (nameof(ExistingFileRule.Overwrite), Loc.T($"extract.rule.overwrite"))
        };
        p.Children.Add(Card(
            Label(Loc.T($"win.options.downloads.integrity_card")),
            Check(Loc.T($"win.options.downloads.auto_discover_hash"), () => _working.AutoDiscoverHash, v => _working.AutoDiscoverHash = v),
            Check(Loc.T($"win.options.downloads.auto_extract"), () => _working.AutoExtractEnabled, v => _working.AutoExtractEnabled = v),
            Label(Loc.T($"win.options.downloads.extract_target")),
            Combo(targetChoices, _working.AutoExtractTargetMode.ToString(), v =>
            {
                if (Enum.TryParse<ExtractTargetMode>(v, out var mode))
                    _working.AutoExtractTargetMode = mode;
            }),
            Label(Loc.T($"win.options.downloads.extract_existing")),
            Combo(ruleChoices, _working.AutoExtractExistingRule.ToString(), v =>
            {
                if (Enum.TryParse<ExistingFileRule>(v, out var rule))
                    _working.AutoExtractExistingRule = rule;
            }),
            Check(Loc.T($"win.options.downloads.delete_archive"), () => _working.AutoExtractDeleteArchive, v => _working.AutoExtractDeleteArchive = v),
            Label(Loc.T($"win.options.downloads.custom_7zip")),
            PathBox(_working.Custom7ZipPath, v => _working.Custom7ZipPath = v, Loc.T("win.options.downloads.av_filter"))));

        p.Children.Add(Card(
            Check(Loc.T("win.options.downloads.av_enable"), () => _working.VirusScanEnabled, v => _working.VirusScanEnabled = v),
            Label(Loc.T("win.options.downloads.av_scanner")),
            PathBox(_working.VirusScanner, v => _working.VirusScanner = v, Loc.T("win.options.downloads.av_filter")),
            Label(Loc.T("win.options.downloads.av_args")),
            Text(_working.VirusArgs, v => _working.VirusArgs = v, true),
            Label(Loc.T("win.options.downloads.av_ext")),
            Text(_working.VirusExtensions, v => _working.VirusExtensions = v, true)));
        return p;
    }

    StackPanel LoginsPage()
    {
        var p = Page(Loc.T("win.options.logins.title"), Loc.T("win.options.logins.note"));
        var itemTemplate = new DataTemplate(typeof(SiteLogin));
        var rowFactory = new FrameworkElementFactory(typeof(DockPanel));
        var userFactory = new FrameworkElementFactory(typeof(TextBlock));
        userFactory.SetValue(DockPanel.DockProperty, Dock.Right);
        userFactory.SetValue(TextBlock.ForegroundProperty, Brush("Muted"));
        userFactory.SetValue(TextBlock.MarginProperty, new Thickness(12, 0, 0, 0));
        userFactory.SetValue(TextBlock.FlowDirectionProperty, FlowDirection.LeftToRight);
        userFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(SiteLogin.User)));
        rowFactory.AppendChild(userFactory);
        var patternFactory = new FrameworkElementFactory(typeof(TextBlock));
        patternFactory.SetValue(TextBlock.FlowDirectionProperty, FlowDirection.LeftToRight);
        patternFactory.SetBinding(TextBlock.TextProperty, new Binding(nameof(SiteLogin.Pattern)));
        rowFactory.AppendChild(patternFactory);
        itemTemplate.VisualTree = rowFactory;

        var list = new ListBox
        {
            MinHeight = 150,
            ItemTemplate = itemTemplate
        };
        list.ItemsSource = _working.Logins;
        var buttons = EditorButtons(
            () =>
            {
                var x = new SiteLogin();
                if (EditLogin(x, isNew: true))
                {
                    _working.Logins.Add(x);
                }
                list.Items.Refresh();
            },
            () =>
            {
                if (list.SelectedItem is SiteLogin x && EditLogin(x, isNew: false))
                {
                    list.Items.Refresh();
                }
            },
            () =>
            {
                if (list.SelectedItem is SiteLogin x)
                {
                    _working.Logins.Remove(x);
                }
                list.Items.Refresh();
            });
        p.Children.Add(Card(Label(Loc.T("win.options.logins.list_label")), list, buttons));
        return p;
    }

    bool EditLogin(SiteLogin x, bool isNew = false)
    {
        var patternVal = x.Pattern;
        var userVal = x.User;
        var passBox = new PasswordBox { FlowDirection = FlowDirection.LeftToRight };

        var dlg = new Window
        {
            Owner = this,
            Title = Loc.T("win.options.logins.dialog_title"),
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)FindResource("GdmWindow")
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label(Loc.T("win.options.logins.pattern_label")));
        panel.Children.Add(Text(patternVal, v => patternVal = v, true));
        panel.Children.Add(Label(Loc.T("win.options.logins.user_label")));
        panel.Children.Add(Text(userVal, v => userVal = v, true));
        panel.Children.Add(Label(Loc.T("win.options.logins.password_label")));
        panel.Children.Add(passBox);
        if (!isNew && !string.IsNullOrEmpty(x.Password))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("win.options.logins.password_keep_hint"),
                Foreground = Brush("Muted"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = Loc.T("win.options.ok"), Style = (Style)FindResource("Primary"), IsDefault = true, Padding = new Thickness(20, 7, 20, 7) };
        var cancel = new Button { Content = Loc.T("win.options.cancel"), IsCancel = true, Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(patternVal)) return;
            dlg.DialogResult = true;
        };
        actions.Children.Add(ok);
        actions.Children.Add(cancel);
        panel.Children.Add(actions);
        dlg.Content = panel;

        if (dlg.ShowDialog() != true)
        {
            return false;
        }
        x.Pattern = patternVal.Trim();
        x.User = userVal.Trim();
        if (passBox.Password.Length > 0 || isNew)
        {
            x.Password = passBox.Password;
        }
        return true;
    }
}
