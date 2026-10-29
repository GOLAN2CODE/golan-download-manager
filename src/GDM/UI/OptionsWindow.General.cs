using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;
using Golan.Gdm.Integration;
using Microsoft.Win32;

namespace Golan.Gdm.UI;

public partial class OptionsWindow
{
    StackPanel GeneralPage()
    {
        var p = Page(Loc.T("win.options.general.title"), Loc.T("win.options.general.note"));
        var langChoices = new[]
        {
            ("auto", Loc.T("win.options.general.lang.auto")),
            ("ar", Loc.T("win.options.general.lang.ar")),
            ("en", Loc.T("win.options.general.lang.en"))
        };
        var langNote = new TextBlock
        {
            Text = Loc.T("win.options.general.lang.note"),
            Foreground = Brush("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var runOnStartupCheck = Check(Loc.T("win.options.general.run_on_startup"), () => _working.StartWithWindows, v => _working.StartWithWindows = v);
        if (AppPaths.IsPortable)
            runOnStartupCheck.IsEnabled = false;
        p.Children.Add(Card(
            runOnStartupCheck,
            Check(Loc.T("win.options.general.clipboard"), () => _working.ClipboardMonitor, v => _working.ClipboardMonitor = v),
            Check(Loc.T("win.options.general.minimize_to_tray"), () => _working.CloseToTray, v => _working.CloseToTray = v),
            Check(Loc.T("win.options.general.intercept_browser"), () => _working.InterceptBrowser, v => _working.InterceptBrowser = v),
            Check(Loc.T($"win.options.general.keep_awake"), () => _working.KeepSystemAwake, v => _working.KeepSystemAwake = v),
            Check(Loc.T($"win.options.general.keep_display"), () => _working.KeepDisplayOn, v => _working.KeepDisplayOn = v),
            Check(Loc.T($"win.options.general.show_speed_graph"), () => _working.ShowSpeedGraph, v => _working.ShowSpeedGraph = v),
            Label(Loc.T("win.options.general.language")),
            Combo(langChoices, string.IsNullOrWhiteSpace(_working.Language) ? "auto" : _working.Language, v => _working.Language = v),
            langNote));
        var menus = new StackPanel();
        var menuDefs = new[]
        {
            ("link", Loc.T("win.options.general.menu.link")),
            ("all", Loc.T("win.options.general.menu.all")),
            ("video", Loc.T("win.options.general.menu.video")),
            ("image", Loc.T("win.options.general.menu.image"))
        };
        foreach (var (key, text) in menuDefs)
        {
            menus.Children.Add(Check(text, () => _working.ExtensionMenuItems.Contains(key), v =>
            {
                if (v && !_working.ExtensionMenuItems.Contains(key))
                {
                    _working.ExtensionMenuItems.Add(key);
                }
                if (!v)
                {
                    _working.ExtensionMenuItems.Remove(key);
                }
            }));
        }
        var hint = new TextBlock
        {
            Text = Loc.T("win.options.general.keys_hint"),
            Foreground = Brush("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        p.Children.Add(Card(Label(Loc.T("win.options.general.ext_menu_label")), menus, hint));
        return p;
    }

    StackPanel FileTypesPage()
    {
        var p = Page(Loc.T("win.options.filetypes.title"), Loc.T("win.options.filetypes.note"));
        var ext = Text(_working.CatchExtensions, v => _working.CatchExtensions = v, true);
        ext.AcceptsReturn = true;
        ext.TextWrapping = TextWrapping.Wrap;
        ext.MinHeight = 96;
        var reset = new Button
        {
            Content = Loc.T("win.options.filetypes.default_btn"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        reset.Click += (_, _) =>
        {
            _working.CatchExtensions = new AppSettings().CatchExtensions;
            ext.Text = _working.CatchExtensions;
        };
        p.Children.Add(Card(
            Label(Loc.T("win.options.filetypes.caught_ext")),
            ext,
            reset,
            Label(Loc.T("win.options.filetypes.min_size_mb")),
            Number(_working.MinSizeMB, v => _working.MinSizeMB = v)));
        p.Children.Add(Card(
            Label(Loc.T("win.options.filetypes.exception_sites")),
            Text(_working.ExceptionSites, v => _working.ExceptionSites = v, true),
            Label(Loc.T("win.options.filetypes.exception_addrs")),
            Text(_working.ExceptionAddresses, v => _working.ExceptionAddresses = v, true),
            Label(Loc.T("win.options.filetypes.autostart_ext")),
            Text(_working.AutoStartExtensions, v => _working.AutoStartExtensions = v, true),
            Check(Loc.T("win.options.filetypes.twice_cancel"), () => _working.TwiceCancelPrompt, v => _working.TwiceCancelPrompt = v)));
        return p;
    }

    StackPanel SaveToPage()
    {
        var p = Page(Loc.T("win.options.save.title"), Loc.T("win.options.save.note"));
        var categories = new ListBox
        {
            MinHeight = 150,
            DisplayMemberPath = "Name"
        };
        categories.ItemsSource = _working.CategoryDefs;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var add = new Button { Content = Loc.T("win.options.add") };
        var edit = new Button { Content = Loc.T("win.options.edit"), Margin = new Thickness(6, 0, 0, 0) };
        var del = new Button { Content = Loc.T("win.options.delete"), Margin = new Thickness(6, 0, 0, 0) };
        buttons.Children.Add(add);
        buttons.Children.Add(edit);
        buttons.Children.Add(del);
        add.Click += (_, _) => EditCategory(null, categories);
        edit.Click += (_, _) => EditCategory(categories.SelectedItem as CategoryDef, categories);
        del.Click += (_, _) =>
        {
            if (categories.SelectedItem is CategoryDef c && !c.BuiltIn)
            {
                _working.CategoryDefs.Remove(c);
            }
            categories.Items.Refresh();
        };
        p.Children.Add(Card(
            Label(Loc.T("win.options.save.categories")),
            categories,
            buttons,
            Label(Loc.T("win.options.save.download_root")),
            PathBox(_working.DownloadRoot, v => _working.DownloadRoot = v),
            Check(Loc.T("win.options.save.use_cat_folders"), () => _working.UseCategoryFolders, v => _working.UseCategoryFolders = v),
            Label(Loc.T("win.options.save.temp_dir")),
            PathBox(_working.TempDir, v => _working.TempDir = v),
            Check(Loc.T("win.options.save.server_file_time"), () => _working.UseServerFileTime, v => _working.UseServerFileTime = v)));
        return p;
    }

    void EditCategory(CategoryDef? current, ListBox list)
    {
        var c = current ?? new CategoryDef { Key = Categories.NewKey(), Name = Loc.T("win.options.save.cat.new_default") };
        var nameVal = c.Name;
        var extVal = c.Extensions;
        var sitesVal = c.Sites;
        var folderVal = c.Folder;
        var rememberVal = c.RememberLastPath;

        var dlg = new Window
        {
            Owner = this,
            Title = Loc.T("win.options.save.cat.dialog_title"),
            Width = 480,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)FindResource("GdmWindow")
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Label(Loc.T("win.options.save.cat.name")));
        panel.Children.Add(Text(nameVal, v => nameVal = v));
        panel.Children.Add(Label(Loc.T("win.options.save.cat.extensions")));
        panel.Children.Add(Text(extVal, v => extVal = v, true));
        panel.Children.Add(Label(Loc.T("win.options.save.cat.sites")));
        panel.Children.Add(Text(sitesVal, v => sitesVal = v, true));
        panel.Children.Add(Label(Loc.T("win.options.save.cat.folder")));
        panel.Children.Add(PathBox(folderVal, v => folderVal = v));
        panel.Children.Add(Check(Loc.T("win.options.save.cat.remember_last"), () => rememberVal, v => rememberVal = v));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = Loc.T("win.options.ok"), Style = (Style)FindResource("Primary"), IsDefault = true, Padding = new Thickness(20, 7, 20, 7) };
        var cancel = new Button { Content = Loc.T("win.options.cancel"), IsCancel = true, Padding = new Thickness(20, 7, 20, 7), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(nameVal)) return;
            dlg.DialogResult = true;
        };
        actions.Children.Add(ok);
        actions.Children.Add(cancel);
        panel.Children.Add(actions);
        dlg.Content = panel;

        if (dlg.ShowDialog() != true)
        {
            return;
        }
        c.Name = nameVal.Trim();
        c.Extensions = extVal.Trim();
        c.Sites = sitesVal.Trim();
        c.Folder = folderVal.Trim();
        c.RememberLastPath = rememberVal;
        if (current == null)
        {
            _working.CategoryDefs.Insert(Math.Max(0, _working.CategoryDefs.Count - 1), c);
        }
        list.Items.Refresh();
    }

    StackPanel SoundsPage()
    {
        var p = Page(Loc.T("win.options.sounds.title"), Loc.T("win.options.sounds.note"));
        p.Children.Add(Card(Check(Loc.T("win.options.sounds.enable"), () => _working.SoundsEnabled, v => _working.SoundsEnabled = v)));
        var soundEvents = new[]
        {
            ("complete", Loc.T("win.options.sounds.complete")),
            ("failed", Loc.T("win.options.sounds.failed")),
            ("queueStart", Loc.T("win.options.sounds.queue_start")),
            ("queueDone", Loc.T("win.options.sounds.queue_done"))
        };
        foreach (var (key, label) in soundEvents)
        {
            var row = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(0, 4, 0, 4)
            };
            var test = new Button
            {
                Content = Loc.T("win.options.sounds.test"),
                Padding = new Thickness(12, 5, 12, 5)
            };
            DockPanel.SetDock(test, Dock.Left);
            var browse = new Button
            {
                Content = Loc.T("win.options.browse"),
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(6, 0, 0, 0)
            };
            DockPanel.SetDock(browse, Dock.Left);
            var box = Text(_working.Sounds.GetValueOrDefault(key) ?? "", v => _working.Sounds[key] = v, true);
            browse.Click += (_, _) =>
            {
                var d = new OpenFileDialog { Filter = Loc.T("win.options.sounds.wav_filter") };
                if (d.ShowDialog(this) == true)
                {
                    box.Text = d.FileName;
                }
            };
            test.Click += (_, _) =>
            {
                var path = box.Text;
                if (File.Exists(path))
                {
                    new SoundPlayer(path).Play();
                }
                else
                {
                    SystemSounds.Asterisk.Play();
                }
            };
            row.Children.Add(test);
            row.Children.Add(browse);
            row.Children.Add(box);
            p.Children.Add(Card(Label(label), row));
        }
        return p;
    }

    StackPanel BrowsersPage()
    {
        var p = Page(Loc.T("win.options.browsers.title"), Loc.T("win.options.browsers.note"));
        var registered = BrowserIntegration.IsRegistered();
        var statusText = new TextBlock
        {
            Text = registered ? Loc.T("win.options.browsers.ready") : Loc.T("win.options.browsers.not_ready"),
            Foreground = Brush(registered ? "Success" : "Warning"),
            FontWeight = FontWeights.SemiBold
        };
        var instructions = new TextBlock
        {
            Text = Loc.T("win.options.browsers.instructions"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush("Muted"),
            Margin = new Thickness(0, 8, 0, 0)
        };
        p.Children.Add(Card(statusText, instructions, new Button { Content = Loc.T("win.options.browsers.open_folder") }));
        var openFolder = (Button)((StackPanel)((Border)p.Children[^1]).Child).Children[^1];
        openFolder.Click += (_, _) => BrowserIntegration.OpenExtensionFolder();
        var path = Text(AppPaths.ExtensionDir, _ => { }, true);
        path.IsReadOnly = true;
        var copy = new Button
        {
            Content = Loc.T("win.options.browsers.copy"),
            Padding = new Thickness(12, 5, 12, 5)
        };
        copy.Click += (_, _) =>
        {
            App.Instance.MainView?.SuppressClipboard();
            Clipboard.SetText(AppPaths.ExtensionDir);
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(copy, Dock.Left);
        row.Children.Add(copy);
        row.Children.Add(path);
        p.Children.Add(Card(Label(Loc.T("win.options.browsers.ext_dir")), row));
        var xpiPath = Text(BrowserIntegration.FirefoxXpiPath, _ => { }, true);
        xpiPath.IsReadOnly = true;
        var copyXpi = new Button
        {
            Content = Loc.T("win.options.browsers.copy"),
            Padding = new Thickness(12, 5, 12, 5)
        };
        copyXpi.Click += (_, _) =>
        {
            App.Instance.MainView?.SuppressClipboard();
            Clipboard.SetText(BrowserIntegration.FirefoxXpiPath);
        };
        var xpiRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(copyXpi, Dock.Left);
        xpiRow.Children.Add(copyXpi);
        xpiRow.Children.Add(xpiPath);
        p.Children.Add(Card(Label(Loc.T("win.options.browsers.firefox_xpi")), xpiRow));
        foreach (var browser in BrowserIntegration.InstalledBrowsers())
        {
            var open = new Button
            {
                Content = Loc.T("guide.addTo", browser.Name),
                Tag = browser,
                Margin = new Thickness(8, 0, 0, 0),
            };
            open.SetResourceReference(StyleProperty, "Primary");
            System.Windows.Automation.AutomationProperties.SetAutomationId(open, "AddGolanTo_" + Path.GetFileNameWithoutExtension(browser.ExePath ?? "browser"));
            open.Click += (_, _) => App.Instance.ShowBrowserGuide(this, Path.GetFileName(browser.ExePath ?? ""));
            var browserLabel = new TextBlock
            {
                Text = browser.Name,
                VerticalAlignment = VerticalAlignment.Center
            };
            p.Children.Add(Card(new DockPanel { Children = { browserLabel, open } }));
        }
        return p;
    }
}
