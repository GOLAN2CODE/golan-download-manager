using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;
using Golan.Gdm.UI;

namespace Golan.Gdm.Tests.Render;

public static class WindowHarnesses
{
    static readonly string[] OptionTabs =
    [
        "general", "filetypes", "saveto", "downloads", "connection",
        "proxy", "logins", "sounds", "panel", "browsers",
        "video", "torrent", "remote", "updates"
    ];

    public static List<RenderedOutput> RenderAllVariants(string outDir)
    {
        var results = new List<RenderedOutput>();
        var variants = new (string Lang, bool Dark, string ThemeName)[]
        {
            ("ar", false, "light"),
            ("ar", true, "dark"),
            ("en", false, "light"),
            ("en", true, "dark"),
        };

        foreach (var (lang, dark, theme) in variants)
        {
            Console.WriteLine($"Rendering variant {lang}.{theme}...");
            WindowRenderer.ApplyVariant(lang, dark);
            var env = WindowRenderer.Env!;

            // 1. Main (default, 1366x768, 1920x1080)
            results.Add(RenderMain(env, "Main", lang, theme, outDir, new Size(1180, 680)));
            results.Add(RenderMain(env, "Main_1366x768", lang, theme, outDir, new Size(1366, 768)));
            results.Add(RenderMain(env, "Main_1920x1080", lang, theme, outDir, new Size(1920, 1080)));

            // 2. AddDownload
            results.Add(RenderAddDownload(env, lang, theme, outDir));

            // 3. Progress (HTTP & Torrent)
            results.Add(RenderProgress(env.ActiveHttpItem, "Progress", lang, theme, outDir));
            results.Add(RenderProgress(env.ActiveTorrentItem, "Progress_Torrent", lang, theme, outDir));

            // 4. Complete
            results.Add(WindowRenderer.RenderWindow(
                new CompleteWindow(env.CompletedZipItem),
                "Complete", lang, theme, outDir));

            // 5. Properties
            results.Add(WindowRenderer.RenderWindow(
                new PropertiesWindow(env.CompletedZipItem, env.Manager),
                "Properties", lang, theme, outDir));

            // 6. Duplicate
            results.Add(WindowRenderer.RenderWindow(
                new DuplicateWindow(env.CompletedZipItem, env.DuplicateIncomingItem),
                "Duplicate", lang, theme, outDir));

            // 7. Options (all 14 tabs)
            results.AddRange(RenderAllOptionsTabs(env, lang, theme, outDir));

            // 8. Scheduler (Schedule tab + Files tab)
            results.AddRange(RenderScheduler(env, lang, theme, outDir));

            // 9. SpeedLimiter
            results.Add(WindowRenderer.RenderWindow(
                new SpeedLimiterWindow(env.Settings),
                "SpeedLimiter", lang, theme, outDir));

            // 10. SpeedRules
            results.Add(WindowRenderer.RenderWindow(
                new SpeedRulesWindow(env.Settings, Golan.Gdm.App.Instance.SpeedRuleController),
                "SpeedRules", lang, theme, outDir));

            // 11. QueuePicker
            results.Add(WindowRenderer.RenderWindow(
                new QueuePickerWindow(env.Manager, 4),
                "QueuePicker", lang, theme, outDir));

            // 12. Grabber (Steps 1..4 + Results view)
            results.AddRange(RenderGrabber(env, lang, theme, outDir));

            // 13. Batch
            results.Add(WindowRenderer.RenderWindow(
                new BatchWindow(env.Manager, env.SampleBatchLinks, "https://example.com/releases", "Mozilla/5.0"),
                "Batch", lang, theme, outDir));

            // 14. BatchPattern
            results.Add(WindowRenderer.RenderWindow(
                new BatchPatternWindow(env.Manager),
                "BatchPattern", lang, theme, outDir,
                afterLoaded: w =>
                {
                    GetControl<TextBox>(w, "PatternBox").Text = "https://mirror.example.com/archive-part-*.zip";
                    GetControl<TextBox>(w, "FromBox").Text = "1";
                    GetControl<TextBox>(w, "ToBox").Text = "10";
                    GetControl<TextBox>(w, "WidthBox").Text = "2";
                }));

            // 15. Video (with sample media info, subtitles, playlist, qualities, direct streams)
            results.Add(RenderVideo(env, lang, theme, outDir));

            // 16. Columns
            results.Add(RenderColumns(env, lang, theme, outDir));

            // 17. Export
            results.Add(WindowRenderer.RenderWindow(
                new ExportWindow(selectedCount: 3, queueCount: 2),
                "Export", lang, theme, outDir));

            // 18. Find
            results.Add(WindowRenderer.RenderWindow(
                new FindWindow("ubuntu-24.04"),
                "Find", lang, theme, outDir));

            // 19. ZipPreview
            results.Add(RenderZipPreview(env, lang, theme, outDir));

            // 20. DropTarget
            results.Add(WindowRenderer.RenderWindow(
                new DropTargetWindow(),
                "DropTarget", lang, theme, outDir,
                explicitSize: new Size(72, 72)));

            // 21. Welcome
            results.Add(WindowRenderer.RenderWindow(
                new WelcomeWindow(),
                "Welcome", lang, theme, outDir));

            // 21b. Activation (licence) and the browser guide
            results.Add(WindowRenderer.RenderWindow(
                new ActivationWindow(),
                "Activation", lang, theme, outDir));
            results.Add(WindowRenderer.RenderWindow(
                new BrowserGuideWindow("chrome.exe"),
                "BrowserGuide", lang, theme, outDir));

            // 21c. Refresh download address (waiting for the user's click on the site)
            if (string.IsNullOrEmpty(env.ErrorItem.ParentPage)) env.ErrorItem.ParentPage = "https://files.example.com/downloads/page";
            Golan.Gdm.App.Instance.AddressRefresh.Begin(env.ErrorItem.Id, DateTime.UtcNow);
            results.Add(WindowRenderer.RenderWindow(
                new RefreshAddressWindow(env.ErrorItem),
                "RefreshAddress", lang, theme, outDir));

            // 22. Hash
            results.Add(RenderHash(env, lang, theme, outDir));

            // 23. Stats
            results.Add(WindowRenderer.RenderWindow(
                new StatsWindow(env.Manager.Stats),
                "Stats", lang, theme, outDir));

            // 24. TorrentFiles
            results.Add(WindowRenderer.RenderWindow(
                new TorrentFilesWindow("Debian-12.7.0-amd64-DVD-1", env.SampleTorrentFiles, sequential: true, env.ActiveTorrentItem),
                "TorrentFiles", lang, theme, outDir));

            // 25. IdmImport
            results.Add(RenderIdmImport(env, lang, theme, outDir));

            // 26. Remote
            results.Add(RenderRemote(env, lang, theme, outDir));

            // 27. Message Dialogs (Confirm, InputDialog, DeleteConfirm)
            results.Add(WindowRenderer.RenderModalDialog(
                owner => Confirm.Ask(owner, Loc.T("main.deleteCompleted.title"), Loc.T("main.deleteCompleted.body", 5), Loc.T("main.deleteCompleted.yes"), danger: true),
                "Confirm", lang, theme, outDir));

            results.Add(WindowRenderer.RenderModalDialog(
                owner => InputDialog.Ask(owner, Loc.T("main.changeUrl.title"), Loc.T("main.changeUrl.body"), "https://releases.ubuntu.com/24.04/ubuntu-24.04.1-desktop-amd64.iso"),
                "InputDialog", lang, theme, outDir));

            results.Add(WindowRenderer.RenderModalDialog(
                owner => DeleteConfirm.Ask(owner, [env.ActiveHttpItem], deleteFiles: true),
                "DeleteConfirm", lang, theme, outDir));
        }

        return results;
    }

    static RenderedOutput RenderMain(SeededEnvironment env, string key, string lang, string theme, string outDir, Size size)
    {
        env.Settings.ShowSpeedGraph = true;
        var mw = new MainWindow();
        return WindowRenderer.RenderWindow(mw, key, lang, theme, outDir, size, afterLoaded: w =>
        {
            var main = (MainWindow)w;
            main.UpdateSpeedGraphVisibility();
            InvokePrivate(main, "OnTick");
            InvokePrivate(main, "UpdateCounts");
            InvokePrivate(main, "RefreshToolbarState");
        });
    }

    static RenderedOutput RenderAddDownload(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var item = env.CompletedZipItem;
        var win = new AddDownloadWindow(item);
        // Prevent network probe on Loaded
        GetControl<TextBox>(win, "UrlBox").Text = "";
        SampleDataSeeder.StopPrivateTimer(win, "_debounce");

        return WindowRenderer.RenderWindow(win, "AddDownload", lang, theme, outDir, afterLoaded: w =>
        {
            SetPrivateField(w, "_settingName", true);
            GetControl<TextBox>(w, "UrlBox").Text = item.Url;
            SampleDataSeeder.StopPrivateTimer(w, "_debounce");
            CancelPrivateCts(w, "_probeCts");
            GetControl<TextBox>(w, "NameBox").Text = item.FileName;
            SetPrivateField(w, "_settingName", false);
            GetControl<TextBlock>(w, "InfoText").Text = $"{Loc.T("main.add.sizeValue", Fmt.Size(1524L * 1024 * 1024))}  ·  {Loc.T("main.add.resumable")}";
            GetControl<Button>(w, "PreviewBtn").Visibility = Visibility.Visible;
            InvokePrivate(w, "UpdateSpaceHint");
        });
    }

    static RenderedOutput RenderProgress(DownloadItem item, string key, string lang, string theme, string outDir)
    {
        var win = new ProgressWindow(item);
        SampleDataSeeder.StopPrivateTimer(win, "_timer");
        return WindowRenderer.RenderWindow(win, key, lang, theme, outDir, afterLoaded: w =>
        {
            SampleDataSeeder.StopPrivateTimer(w, "_timer");
            InvokePrivate(w, "Refresh");
        });
    }

    static List<RenderedOutput> RenderAllOptionsTabs(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var list = new List<RenderedOutput>();
        var win = new OptionsWindow(env.Manager, "general");
        SetPrivateField(win, "_ensureVideoVersionLoaded", null);
        WindowRenderer.PrepareOffscreenWindow(win);

        try
        {
            win.Show();
            WindowRenderer.PumpDispatcher(win.Dispatcher);

            var navList = GetControl<ListBox>(win, "NavList");
            var pageScroll = GetControl<ScrollViewer>(win, "PageScroll");
            var navItems = navList.Items.Cast<object>().ToList();
            for (int i = 0; i < OptionTabs.Length && i < navItems.Count; i++)
            {
                string tabKey = OptionTabs[i];
                navList.SelectedItem = navItems[i];
                pageScroll.ScrollToTop();
                WindowRenderer.PumpDispatcher(win.Dispatcher);

                var rendered = WindowRenderer.CaptureAndInspect(win, $"Options_{tabKey}", lang, theme, outDir);
                list.Add(rendered);

                if (tabKey == "general")
                {
                    string aliasPath = Path.Combine(outDir, $"Options.{lang}.{theme}.png");
                    File.Copy(rendered.PngPath, aliasPath, overwrite: true);
                }
            }
        }
        finally
        {
            WindowRenderer.CloseCleanly(win);
        }

        return list;
    }

    static List<RenderedOutput> RenderScheduler(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var list = new List<RenderedOutput>();
        var win = new SchedulerWindow(env.Manager, QueueDef.MainId);
        WindowRenderer.PrepareOffscreenWindow(win);

        try
        {
            win.Show();
            WindowRenderer.PumpDispatcher(win.Dispatcher);

            var queueTabs = GetControl<TabControl>(win, "QueueTabs");
            queueTabs.SelectedIndex = 0;
            WindowRenderer.PumpDispatcher(win.Dispatcher);
            list.Add(WindowRenderer.CaptureAndInspect(win, "Scheduler", lang, theme, outDir));

            queueTabs.SelectedIndex = 1;
            WindowRenderer.PumpDispatcher(win.Dispatcher);
            list.Add(WindowRenderer.CaptureAndInspect(win, "Scheduler_Files", lang, theme, outDir));
        }
        finally
        {
            WindowRenderer.CloseCleanly(win);
        }

        return list;
    }

    static List<RenderedOutput> RenderGrabber(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var list = new List<RenderedOutput>();
        var win = new GrabberWindow(env.Manager, "https://releases.ubuntu.com/24.04/");
        SampleDataSeeder.StopPrivateTimer(win, "_statsTimer");
        GetControl<TextBox>(win, "NameBox").Text = "Ubuntu 24.04 Mirrors";
        GetControl<TextBox>(win, "MasksBox").Text = "*.iso,*.zip,*.sha256";
        WindowRenderer.PrepareOffscreenWindow(win);

        try
        {
            win.Show();
            WindowRenderer.PumpDispatcher(win.Dispatcher);

            for (int step = 1; step <= 4; step++)
            {
                SetPrivateField(win, "_step", step);
                InvokePrivate(win, "ShowStep");
                WindowRenderer.PumpDispatcher(win.Dispatcher);
                var stepOut = WindowRenderer.CaptureAndInspect(win, $"Grabber_Step{step}", lang, theme, outDir);
                list.Add(stepOut);
                if (step == 1)
                {
                    File.Copy(stepOut.PngPath, Path.Combine(outDir, $"Grabber.{lang}.{theme}.png"), overwrite: true);
                }
            }

            // Results view
            GetControl<FrameworkElement>(win, "WizardScroll").Visibility = Visibility.Collapsed;
            GetControl<FrameworkElement>(win, "ResultsView").Visibility = Visibility.Visible;
            GetControl<Button>(win, "NextButton").Visibility = Visibility.Collapsed;
            GetControl<Button>(win, "StartButton").Visibility = Visibility.Collapsed;
            var backBtn = GetControl<Button>(win, "BackButton");
            backBtn.Visibility = Visibility.Visible;
            backBtn.IsEnabled = true;
            GetControl<Button>(win, "StopButton").IsEnabled = false;

            InvokePrivate(win, "AddFile", new GrabbedFile
            {
                Url = "https://releases.ubuntu.com/24.04/ubuntu-24.04.1-desktop-amd64.iso",
                FoundOn = "https://releases.ubuntu.com/24.04/",
                Size = 1524L * 1024 * 1024,
                Status = Loc.T("win.grabber.file_added_status"),
                Checked = true,
            });
            InvokePrivate(win, "AddFile", new GrabbedFile
            {
                Url = "https://releases.ubuntu.com/24.04/ubuntu-24.04.1-live-server-amd64.iso",
                FoundOn = "https://releases.ubuntu.com/24.04/",
                Size = 2640L * 1024 * 1024,
                Status = Loc.T("win.grabber.file_added_status"),
                Checked = true,
            });
            var foundTree = GetControl<TreeView>(win, "FoundTree");
            foreach (var tvi in foundTree.Items.OfType<TreeViewItem>())
                tvi.IsExpanded = true;

            GetControl<TextBlock>(win, "PagesText").Text = "12";
            GetControl<TextBlock>(win, "ExploredText").Text = "38";
            GetControl<TextBlock>(win, "MatchedText").Text = "2";
            GetControl<TextBlock>(win, "DownloadedText").Text = "0";
            GetControl<TextBlock>(win, "StatusText").Text = Loc.T("core.grabber.done");
            WindowRenderer.PumpDispatcher(win.Dispatcher);

            list.Add(WindowRenderer.CaptureAndInspect(win, "Grabber_Results", lang, theme, outDir));
        }
        finally
        {
            WindowRenderer.CloseCleanly(win);
        }

        return list;
    }

    static RenderedOutput RenderVideo(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var win = new VideoWindow(
            env.Manager,
            url: "",
            referrer: "https://www.youtube.com/",
            userAgent: "Mozilla/5.0",
            cookies: null,
            media: env.SampleDetectedMedia,
            title: env.SampleMediaInfo.Title);
        SetPrivateField(win, "_loaded", true);

        return WindowRenderer.RenderWindow(win, "Video", lang, theme, outDir, new Size(640, 1280), afterLoaded: w =>
        {
            GetControl<TextBox>(w, "UrlBox").Text = env.SampleMediaInfo.WebpageUrl;
            GetControl<CheckBox>(w, "IncludeAutoSubsBox").IsChecked = true;
            SetPrivateField(w, "_info", env.SampleMediaInfo);
            InvokePrivate(w, "ShowInfo");
            GetControl<Expander>(w, "DirectExpander").IsExpanded = true;
        });
    }

    static RenderedOutput RenderColumns(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var titles = new Dictionary<string, string>
        {
            ["name"] = Loc.T("main.col.name"),
            ["size"] = Loc.T("main.col.size"),
            ["status"] = Loc.T("main.col.status"),
            ["speed"] = Loc.T("main.col.speed"),
            ["eta"] = Loc.T("main.col.eta"),
            ["lastTry"] = Loc.T("main.col.lastTry"),
            ["description"] = Loc.T("main.col.description"),
            ["saveTo"] = Loc.T("main.col.saveTo"),
            ["added"] = Loc.T("main.col.added"),
            ["referrer"] = Loc.T("main.col.referrer"),
            ["parentPage"] = Loc.T("main.col.parentPage"),
        };
        if (env.Settings.Columns.Count == 0)
        {
            env.Settings.Columns = titles.Keys.Select(k => new ColumnPref { Key = k, Visible = true, Width = 110 }).ToList();
        }
        return WindowRenderer.RenderWindow(
            new ColumnsWindow(env.Settings.Columns, titles),
            "Columns", lang, theme, outDir);
    }

    static RenderedOutput RenderZipPreview(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var win = new ZipPreviewWindow(env.CompletedZipItem);
        CancelPrivateCts(win, "_cts");

        return WindowRenderer.RenderWindow(win, "ZipPreview", lang, theme, outDir, afterLoaded: w =>
        {
            var rows = new List<ZipPreviewWindow.ZipEntryRow>
            {
                new("golan-sdk-2.2.0/README.md", Fmt.Size(4280), Fmt.Size(1820), "2026/04/15 10:20"),
                new("golan-sdk-2.2.0/bin/GDM.exe", Fmt.Size(18450000), Fmt.Size(7920000), "2026/04/15 10:22"),
                new("golan-sdk-2.2.0/bin/GDM.dll", Fmt.Size(4120000), Fmt.Size(1640000), "2026/04/15 10:22"),
                new("golan-sdk-2.2.0/extension/gdm-firefox.xpi", Fmt.Size(94200), Fmt.Size(88100), "2026/04/15 10:21"),
            };
            var entriesGrid = GetControl<DataGrid>(w, "EntriesGrid");
            entriesGrid.Visibility = Visibility.Visible;
            entriesGrid.ItemsSource = rows;
            GetControl<TextBlock>(w, "StateText").Text = Loc.T("win.zip.summary", rows.Count, Fmt.Size(22668480), Fmt.Size(9649920));
        });
    }

    static RenderedOutput RenderHash(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var win = new HashWindow(env.SampleRealFilePath, env.SampleRealFileSha256);
        return WindowRenderer.RenderWindow(win, "Hash", lang, theme, outDir, afterLoaded: w =>
        {
            var shaBox = GetControl<TextBox>(w, "Sha256Box");
            var md5Box = GetControl<TextBox>(w, "Md5Box");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while ((string.IsNullOrEmpty(shaBox.Text) || string.IsNullOrEmpty(md5Box.Text)) && sw.ElapsedMilliseconds < 2000)
            {
                Thread.Sleep(5);
                WindowRenderer.PumpDispatcher(w.Dispatcher);
            }
            w.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            GetControl<TextBlock>(w, "StatusLabel").Text = Loc.T("hash.win.done");
        });
    }

    static RenderedOutput RenderIdmImport(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var win = new IdmImportWindow(env.Manager);
        return WindowRenderer.RenderWindow(win, "IdmImport", lang, theme, outDir, afterLoaded: w =>
        {
            InvokePrivate(w, "LoadParseResult", env.SampleIdmParseResult);
        });
    }

    static RenderedOutput RenderRemote(SeededEnvironment env, string lang, string theme, string outDir)
    {
        var win = new RemoteWindow(env.Manager);
        SampleDataSeeder.StopPrivateTimer(win, "_uiTimer");
        return WindowRenderer.RenderWindow(win, "Remote", lang, theme, outDir, afterLoaded: w =>
        {
            SampleDataSeeder.StopPrivateTimer(w, "_uiTimer");
            InvokePrivate(w, "GeneratePin_Click", w, new RoutedEventArgs());
            InvokePrivate(w, "RefreshQrAndStatus");
            InvokePrivate(w, "RefreshLiveState");
        });
    }

    static T GetControl<T>(FrameworkElement root, string name) where T : class
    {
        if (root.FindName(name) is T found) return found;
        var f = root.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (f?.GetValue(root) is T val) return val;
        throw new InvalidOperationException($"Control '{name}' not found on {root.GetType().Name}");
    }

    static object? InvokePrivate(object target, string methodName, params object?[] args)
    {
        var m = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return m?.Invoke(target, args);
    }

    static void SetPrivateField(object target, string fieldName, object? value)
    {
        var f = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        f?.SetValue(target, value);
    }

    static void CancelPrivateCts(object target, string fieldName)
    {
        var f = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (f?.GetValue(target) is CancellationTokenSource cts)
        {
            try { cts.Cancel(); } catch { }
        }
    }
}
