using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Golan.Gdm.Core;
using Golan.Gdm.Integration;

namespace Golan.Gdm.UI;

/// <summary>
/// Reusable card control that runs <see cref="ExtensionAutoInstaller"/> on a background task, displays live per-step
/// progress, allows cancellation, renders per-browser results, and opens <see cref="BrowserGuideWindow"/> on failure.
/// </summary>
public sealed class ExtensionAutoInstallPanel : Border
{
    readonly ExtensionAutoInstaller? _installer;
    readonly IReadOnlyList<BrowserIntegration.BrowserInfo>? _targetBrowsers;
    readonly Action<string?>? _openManualGuide;
    readonly Action<IReadOnlyList<AutoInstallResult>>? _onCompleted;
    readonly bool _inlineManualGuide;
    readonly List<(CheckBox Box, AutoInstallTarget Target)> _targetBoxes = [];
    Task? _scanTask;
    CancellationTokenSource? _cts;
    Window? _hookedWindow;

    public Button InstallButton { get; }
    public Button CancelButton { get; }
    public TextBlock ProgressText { get; }
    public StackPanel ResultsPanel { get; }
    /// <summary>The browsers and profiles found, each with a checkbox: the extension goes only where the user ticks.</summary>
    public StackPanel TargetsPanel { get; }
    public bool IsRunning { get; private set; }

    /// <summary>
    /// The real installer lists profiles and installs into the ticked ones. A panel given its own installer (tests)
    /// and no scanner keeps the old behaviour: one run per browser.
    /// </summary>
    bool UsesTargets => _installer == null || ScanTargetsOverride != null;

    /// <summary>Replaces the real profile search (tests and offscreen renders).</summary>
    public Func<IReadOnlyList<AutoInstallTarget>>? ScanTargetsOverride { get; init; }
    public int TargetCount => _targetBoxes.Count;
    public IReadOnlyList<AutoInstallResult> LastResults { get; private set; } = [];

    public ExtensionAutoInstallPanel(
        ExtensionAutoInstaller? installer = null,
        IReadOnlyList<BrowserIntegration.BrowserInfo>? targetBrowsers = null,
        Action<string?>? openManualGuide = null,
        Action<IReadOnlyList<AutoInstallResult>>? onCompleted = null,
        bool inlineManualGuide = false)
    {
        _installer = installer;
        _targetBrowsers = targetBrowsers;
        _openManualGuide = openManualGuide;
        _onCompleted = onCompleted;
        _inlineManualGuide = inlineManualGuide;

        SetResourceReference(StyleProperty, "Card");
        AutomationProperties.SetAutomationId(this, "ExtensionAutoInstallPanel");

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = Loc.T("autoinstall.ui.title"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });

        var subtitle = new TextBlock
        {
            Text = Loc.T("autoinstall.ui.subtitle"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        stack.Children.Add(subtitle);

        TargetsPanel = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(TargetsPanel, "AutoInstallTargetsPanel");
        // Dozens of profiles are common: the list scrolls inside the card instead of stretching the window.
        stack.Children.Add(new ScrollViewer
        {
            Content = TargetsPanel,
            MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        var buttonRow = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        InstallButton = new Button
        {
            Content = Loc.T("autoinstall.ui.button"),
            Margin = new Thickness(0, 0, 8, 4),
        };
        InstallButton.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        AutomationProperties.SetAutomationId(InstallButton, "AutoInstallExtensionBtn");
        AutomationProperties.SetName(InstallButton, Loc.T("autoinstall.ui.button"));
        InstallButton.Click += (_, _) => _ = StartInstallAsync();
        buttonRow.Children.Add(InstallButton);

        CancelButton = new Button
        {
            Content = Loc.T("autoinstall.ui.cancel"),
            Margin = new Thickness(0, 0, 8, 4),
            Visibility = Visibility.Collapsed,
            IsEnabled = false,
        };
        AutomationProperties.SetAutomationId(CancelButton, "AutoInstallCancelBtn");
        AutomationProperties.SetName(CancelButton, Loc.T("autoinstall.ui.cancel"));
        CancelButton.Click += (_, _) => CancelInstall();
        buttonRow.Children.Add(CancelButton);
        stack.Children.Add(buttonRow);

        ProgressText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(ProgressText, "AutoInstallProgressText");
        stack.Children.Add(ProgressText);

        ResultsPanel = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(ResultsPanel, "AutoInstallResultsPanel");
        stack.Children.Add(ResultsPanel);

        Child = stack;

        Loaded += (_, _) =>
        {
            AttachWindowClose();
            _ = EnsureTargetsAsync();
        };
    }

    /// <summary>Searches the browsers' profiles once (read-only, off the UI thread) and lists them.</summary>
    public Task EnsureTargetsAsync()
    {
        if (!UsesTargets) return Task.CompletedTask;
        return _scanTask ??= ScanTargetsAsync();
    }

    async Task ScanTargetsAsync()
    {
        InstallButton.IsEnabled = false;
        TargetsPanel.Children.Clear();
        _targetBoxes.Clear();
        var searching = new TextBlock { Text = Loc.T("autoinstall.ui.scanning"), TextWrapping = TextWrapping.Wrap };
        searching.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        TargetsPanel.Children.Add(searching);
        TargetsPanel.Visibility = Visibility.Visible;

        IReadOnlyList<AutoInstallTarget> targets;
        try
        {
            var browsers = _targetBrowsers;
            var scan = ScanTargetsOverride ?? (() => ExtensionAutoInstaller.ScanTargets(browsers));
            targets = await Task.Run(scan);
        }
        catch (Exception ex)
        {
            Log.Write("auto-install profile search failed: " + ex.Message);
            targets = [];
        }

        ShowTargets(targets);
        if (!IsRunning) InstallButton.IsEnabled = true;
    }

    /// <summary>
    /// Lists the found browsers and profiles (also used by the offscreen render test). A profile that has no extension
    /// is ticked only when it is the one its browser used last; one that needs an update is ticked too. The rest is
    /// the user's choice.
    /// </summary>
    public void ShowTargets(IReadOnlyList<AutoInstallTarget> targets)
    {
        TargetsPanel.Children.Clear();
        _targetBoxes.Clear();
        TargetsPanel.Visibility = Visibility.Visible;
        if (targets.Count == 0)
        {
            var none = new TextBlock { Text = Loc.T("guide.noBrowsers"), TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
            TargetsPanel.Children.Add(none);
            return;
        }

        TargetsPanel.Children.Add(new TextBlock
        {
            Text = Loc.T("autoinstall.ui.choose"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2),
        });

        // Dozens of profiles: two quick choices instead of ticking them one by one.
        if (targets.Count > 3)
        {
            var quick = new WrapPanel { Margin = new Thickness(0, 4, 0, 2) };
            var all = new Button { Content = Loc.T("autoinstall.ui.select_all"), Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetAutomationId(all, "AutoInstallSelectAllBtn");
            all.Click += (_, _) => { foreach (var (b, t) in _targetBoxes) if (b.IsEnabled) b.IsChecked = !t.Installed || t.NeedsUpdate; };
            var none = new Button { Content = Loc.T("autoinstall.ui.select_none"), Padding = new Thickness(10, 2, 10, 2) };
            AutomationProperties.SetAutomationId(none, "AutoInstallSelectNoneBtn");
            none.Click += (_, _) => { foreach (var (b, _) in _targetBoxes) if (b.IsEnabled) b.IsChecked = false; };
            quick.Children.Add(all);
            quick.Children.Add(none);
            TargetsPanel.Children.Add(quick);
        }

        foreach (var t in targets)
        {
            var (statusKey, brushKey) = t.NeedsUpdate ? ("autoinstall.ui.target.update", "Warning")
                : t.Installed ? ("autoinstall.ui.target.installed", "Success")
                : ("autoinstall.ui.target.missing", "Muted");
            var status = Loc.T(statusKey);

            var line = new WrapPanel();
            line.Children.Add(new TextBlock { Text = t.Label, TextWrapping = TextWrapping.Wrap });
            var statusText = new TextBlock { Text = "  ·  " + status };
            statusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            line.Children.Add(statusText);

            var box = new CheckBox
            {
                Content = line,
                IsChecked = t.NeedsUpdate || (!t.Installed && t.IsLastUsed),
                Margin = new Thickness(0, 5, 0, 0),
            };
            AutomationProperties.SetName(box, t.Label + " · " + status);
            TargetsPanel.Children.Add(box);
            _targetBoxes.Add((box, t));
        }
    }

    string TargetLabel(AutoInstallResult r)
    {
        foreach (var (_, t) in _targetBoxes)
        {
            if (string.Equals(t.Browser.ExePath, r.Browser.ExePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.ProfileDirectory ?? "", r.ProfileDirectory ?? "", StringComparison.OrdinalIgnoreCase))
                return t.Label;
        }
        return r.Browser.Name;
    }

    void AttachWindowClose()
    {
        var win = Window.GetWindow(this);
        if (win == null || ReferenceEquals(win, _hookedWindow))
            return;
        _hookedWindow = win;
        win.Closed += (_, _) => CancelInstall();
    }

    public void CancelInstall()
    {
        if (!IsRunning || _cts is null || _cts.IsCancellationRequested)
            return;
        CancelButton.IsEnabled = false;
        ProgressText.Text = Loc.T("autoinstall.ui.cancelling");
        ProgressText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { /* CTS already disposed on completion */ }
    }

    public async Task<IReadOnlyList<AutoInstallResult>> StartInstallAsync()
    {
        if (IsRunning)
            return LastResults;

        IsRunning = true;
        InstallButton.IsEnabled = false;
        CancelButton.Content = Loc.T("autoinstall.ui.cancel");
        CancelButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Visible;
        ResultsPanel.Children.Clear();
        ResultsPanel.Visibility = Visibility.Collapsed;
        ProgressText.Text = Loc.T("autoinstall.ui.starting");
        ProgressText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
        ProgressText.Visibility = Visibility.Visible;

        // Nothing is installed anywhere the user did not tick.
        List<AutoInstallTarget>? chosen = null;
        if (UsesTargets)
        {
            await EnsureTargetsAsync();
            chosen = _targetBoxes.Where(x => x.Box.IsChecked == true).Select(x => x.Target).ToList();
            if (_targetBoxes.Count > 0 && chosen.Count == 0)
            {
                ResetRunningButtons();
                ProgressText.Text = Loc.T("autoinstall.ui.none_selected");
                ProgressText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
                return LastResults;
            }
            foreach (var (box, _) in _targetBoxes) box.IsEnabled = false;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var dispatcher = Dispatcher;

        Action<AutoInstallProgress> onProgress = p =>
        {
            var line = string.IsNullOrWhiteSpace(p.BrowserName)
                || p.Message.Contains(p.BrowserName, StringComparison.OrdinalIgnoreCase)
                ? p.Message
                : $"{p.BrowserName}: {p.Message}";
            _ = dispatcher.BeginInvoke(() =>
            {
                if (IsRunning && !ct.IsCancellationRequested)
                    ProgressText.Text = line;
            });
        };

        try
        {
            var runner = _installer ?? new ExtensionAutoInstaller();
            var targetBrowsers = _targetBrowsers;

            var results = await Task.Run(async () =>
            {
                if (chosen != null)
                {
                    var list = new List<AutoInstallResult>(chosen.Count);
                    foreach (var target in chosen)
                    {
                        ct.ThrowIfCancellationRequested();
                        var profile = string.IsNullOrEmpty(target.ProfileDirectory) ? null : target.ProfileDirectory;
                        list.Add(await runner.InstallAsync(target.Browser, profile, onProgress, ct).ConfigureAwait(false));
                    }
                    return (IReadOnlyList<AutoInstallResult>)list;
                }
                if (targetBrowsers != null)
                {
                    var list = new List<AutoInstallResult>(targetBrowsers.Count);
                    foreach (var browser in targetBrowsers)
                    {
                        ct.ThrowIfCancellationRequested();
                        list.Add(await runner.InstallAsync(browser, profileDirectory: null, onProgress, ct).ConfigureAwait(false));
                    }
                    return (IReadOnlyList<AutoInstallResult>)list;
                }
                return await runner.InstallAllInstalledAsync(onProgress, ct).ConfigureAwait(false);
            }, ct);

            LastResults = results;
            ResetRunningButtons();

            // A driver step may finish after Cancel (or after the window closed) without observing the token:
            // treat it as cancelled, so no guide window opens over a closed or cancelled panel.
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            if (results.Count == 0)
            {
                ProgressText.Text = Loc.T("guide.noBrowsers");
                ProgressText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
                _onCompleted?.Invoke(results);
                return results;
            }

            var firstFailed = ShowResults(results);
            _onCompleted?.Invoke(results);
            // A browser locked by its organisation cannot be set up by hand either: the guide would only mislead.
            if (firstFailed != null && firstFailed.Status != AutoInstallStatus.BlockedByPolicy)
                OpenManualGuideFor(firstFailed.Browser);

            return results;
        }
        catch (OperationCanceledException)
        {
            ResetRunningButtons();
            ProgressText.Text = Loc.T("autoinstall.ui.cancelled");
            ProgressText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            return LastResults;
        }
        catch (Exception ex)
        {
            Log.Write("auto-install ui failed: " + ex.Message);
            ResetRunningButtons();
            ProgressText.Text = Loc.T("autoinstall.ui.unexpected_error", FriendlyError.Format(ex));
            ProgressText.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            OpenManualGuideFor(null);
            return LastResults;
        }
        finally
        {
            ResetRunningButtons();
            if (UsesTargets)
            {
                // Search again so every profile shows its new state (the result rows already carry their names).
                _scanTask = null;
                _ = EnsureTargetsAsync();
            }
        }
    }

    /// <summary>
    /// Shows the summary line and one row per browser (also used by the offscreen render test).
    /// Returns the first result that needs the user's action, or null when every browser is done.
    /// </summary>
    public AutoInstallResult? ShowResults(IReadOnlyList<AutoInstallResult> results)
    {
        ResultsPanel.Children.Clear();
        foreach (var r in results)
            ResultsPanel.Children.Add(BuildResultRow(r));
        ResultsPanel.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;

        var firstFailed = results.FirstOrDefault(r => !r.Succeeded);
        if (firstFailed != null)
        {
            ProgressText.Text = Loc.T(_inlineManualGuide
                ? "autoinstall.ui.done_needs_action_inline"
                : "autoinstall.ui.done_needs_action");
            ProgressText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        }
        else
        {
            ProgressText.Text = Loc.T("autoinstall.ui.done_all_ok");
            ProgressText.SetResourceReference(TextBlock.ForegroundProperty, "Success");
        }
        return firstFailed;
    }

    void ResetRunningButtons()
    {
        IsRunning = false;
        InstallButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    void OpenManualGuideFor(BrowserIntegration.BrowserInfo? browser)
    {
        var exe = browser?.ExePath is { Length: > 0 } p ? Path.GetFileName(p) : null;
        if (_openManualGuide != null)
        {
            _openManualGuide(string.IsNullOrWhiteSpace(exe) ? null : exe);
            return;
        }
        // Only a visible window can own the guide; a closed host falls back to the main window.
        var host = Window.GetWindow(this);
        App.Instance?.ShowBrowserGuide(host is { IsVisible: true } ? host : null, string.IsNullOrWhiteSpace(exe) ? null : exe);
    }

    UIElement BuildResultRow(AutoInstallResult r)
    {
        var rowBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 0),
            Tag = r,
        };
        rowBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

        var body = new StackPanel();
        var header = new WrapPanel();

        var (statusLabel, brushKey, symbol) = r.Status switch
        {
            AutoInstallStatus.AlreadyInstalled => (Loc.T("autoinstall.ui.status.already_installed"), "Success", "✓"),
            AutoInstallStatus.Installed => (Loc.T("autoinstall.ui.status.installed"), "Success", "✓"),
            AutoInstallStatus.InstalledTemporary => (Loc.T("autoinstall.ui.status.installed_temp"), "Warning", "✓"),
            _ => (Loc.T("autoinstall.ui.status.needs_action"), "Danger", "!"),
        };

        header.Children.Add(new TextBlock
        {
            Text = TargetLabel(r) + " — ",
            FontWeight = FontWeights.SemiBold,
        });

        var statusText = new TextBlock
        {
            Text = $"{symbol} {statusLabel}",
            FontWeight = FontWeights.SemiBold,
        };
        statusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        header.Children.Add(statusText);
        body.Children.Add(header);

        var msgText = new TextBlock
        {
            Text = r.Message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        msgText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        body.Children.Add(msgText);

        if (!r.Succeeded && r.ManualSteps is { Count: > 0 })
        {
            var stepsPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var step in r.ManualSteps)
            {
                stepsPanel.Children.Add(new TextBlock
                {
                    Text = step,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
            body.Children.Add(stepsPanel);

            if (!_inlineManualGuide)
            {
                var guideBtn = new Button
                {
                    Content = Loc.T("autoinstall.ui.open_guide"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                guideBtn.Click += (_, _) => OpenManualGuideFor(r.Browser);
                body.Children.Add(guideBtn);
            }
        }

        rowBorder.Child = body;
        return rowBorder;
    }
}

/// <summary>Dedicated dialog hosting <see cref="ExtensionAutoInstallPanel"/> and first-run offer prompt.</summary>
public sealed class ExtensionAutoInstallWindow : Window
{
    public ExtensionAutoInstallPanel Panel { get; }

    public ExtensionAutoInstallWindow(bool autoStartOnLoad = false)
    {
        Title = Loc.T("autoinstall.ui.title");
        FlowDirection = Loc.FlowDirection;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(460, SystemParameters.WorkArea.Height - 40);
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = Application.Current?.TryFindResource("GdmWindow") as Style;
        AutomationProperties.SetAutomationId(this, "ExtensionAutoInstallWindow");

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        Panel = new ExtensionAutoInstallPanel();
        root.Children.Add(Panel);

        var close = new Button
        {
            Content = Loc.T("guide.close"),
            MinWidth = 100,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 16, 0, 0),
            IsCancel = true,
        };
        AutomationProperties.SetAutomationId(close, "AutoInstallWindowCloseBtn");
        close.Click += (_, _) => Close();
        root.Children.Add(close);

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        if (autoStartOnLoad)
        {
            Loaded += async (_, _) =>
            {
                // With a single place to install into there is nothing to choose; otherwise the user ticks first.
                await Panel.EnsureTargetsAsync();
                if (Panel.TargetCount <= 1 && IsVisible)
                    _ = Panel.StartInstallAsync();
            };
        }
    }

    /// <summary>Opens the auto-install dialog without starting until the user clicks the install button.</summary>
    public static void ShowDialogIdle(Window? owner)
    {
        var win = new ExtensionAutoInstallWindow(autoStartOnLoad: false);
        var visibleOwner = owner is { IsVisible: true } ? owner : null;
        if (visibleOwner != null) win.Owner = visibleOwner;
        else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ShowDialog();
    }

    /// <summary>Shows the auto-install dialog and starts installing immediately after the user pressed "Install now".</summary>
    public static void ShowAndStart(Window? owner)
    {
        var win = new ExtensionAutoInstallWindow(autoStartOnLoad: true);
        var visibleOwner = owner is { IsVisible: true } ? owner : null;
        if (visibleOwner != null) win.Owner = visibleOwner;
        else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ShowDialog();
    }

    /// <summary>
    /// Shows the one-time first-run offer dialog ("Install now" / "Later"), persists the choice in <paramref name="settings"/>,
    /// and returns true only if the user explicitly pressed "Install now". Never runs the installer itself.
    /// </summary>
    public static bool AskFirstRunOffer(
        Window? owner,
        AppSettings settings,
        IReadOnlyList<BrowserIntegration.BrowserInfo>? detectedBrowsers = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ExtensionOfferPolicy.MarkShown(settings, save: true);

        var browsers = detectedBrowsers ?? ExtensionAutoInstaller.InstalledSupportedBrowsers();
        bool installNow = false;
        var w = CreateFirstRunOfferWindow(owner, browsers, choice => installNow = choice);
        w.ShowDialog();

        ExtensionOfferPolicy.RecordChoice(settings, installNow, save: true);
        return installNow;
    }

    /// <summary>
    /// Builds (without showing) the first-run offer dialog. <paramref name="onChoice"/> receives true for
    /// "Install now" and false for "Later"; closing the window any other way leaves the choice as Later.
    /// </summary>
    public static Window CreateFirstRunOfferWindow(
        Window? owner,
        IReadOnlyList<BrowserIntegration.BrowserInfo> browsers,
        Action<bool> onChoice)
    {
        // In Arabic, a right-to-left mark after each Latin browser name keeps the list and its
        // parentheses in reading order instead of one reversed Latin run that wraps out of order.
        var names = browsers.Count == 0 ? ""
            : Loc.IsRtl
                ? string.Join("‏، ", browsers.Select(b => b.Name)) + "‏"
                : string.Join(", ", browsers.Select(b => b.Name));

        var visibleOwner = owner is { IsVisible: true } ? owner : null;
        var w = new Window
        {
            Title = Loc.T("autoinstall.offer.title"),
            FlowDirection = Loc.FlowDirection,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = visibleOwner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            ShowInTaskbar = visibleOwner == null,
            Style = Application.Current?.TryFindResource("GdmWindow") as Style,
        };
        w.SetResourceReference(Control.BackgroundProperty, "Surface");
        if (visibleOwner != null) w.Owner = visibleOwner;
        AutomationProperties.SetAutomationId(w, "FirstRunExtensionOfferDialog");

        var panel = new StackPanel { Margin = new Thickness(24) };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        try
        {
            head.Children.Add(new Image
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Assets/gdm.png")),
                Width = 44,
                Height = 44,
                VerticalAlignment = VerticalAlignment.Top,
            });
        }
        catch { /* pack URI unavailable in headless tests */ }

        var titles = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock
        {
            Text = Loc.T("autoinstall.offer.heading"),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap,
        });
        head.Children.Add(titles);
        panel.Children.Add(head);

        var body = new TextBlock
        {
            Text = Loc.T("autoinstall.offer.body", names),
            TextWrapping = TextWrapping.Wrap,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 20, 0, 0),
        };

        var installBtn = new Button
        {
            Name = "InstallNowBtn",
            Content = Loc.T("autoinstall.offer.install_now"),
            IsDefault = true,
            MinWidth = 120,
        };
        installBtn.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
        AutomationProperties.SetAutomationId(installBtn, "FirstRunOfferInstallNowBtn");
        installBtn.Click += (_, _) => { onChoice(true); w.Close(); };

        var laterBtn = new Button
        {
            Name = "LaterBtn",
            Content = Loc.T("autoinstall.offer.later"),
            IsCancel = true,
            MinWidth = 100,
            Margin = new Thickness(8, 0, 0, 0),
        };
        AutomationProperties.SetAutomationId(laterBtn, "FirstRunOfferLaterBtn");
        laterBtn.Click += (_, _) => { onChoice(false); w.Close(); };

        buttons.Children.Add(installBtn);
        buttons.Children.Add(laterBtn);
        panel.Children.Add(buttons);

        w.Content = panel;
        return w;
    }
}
