using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Golan.Gdm.Core;
using Golan.Gdm.Integration;
using Golan.Gdm.UI;

namespace Golan.Gdm.Tests.Render;

public sealed record RenderedOutput(
    string WindowKey,
    string Variant,
    string Lang,
    string Theme,
    string PngPath,
    int PixelWidth,
    int PixelHeight,
    List<LayoutIssue> Issues);

public static class WindowRenderer
{
    public static SeededEnvironment? Env { get; private set; }

    public static void InitializeApp(string gdmHome)
    {
        if (Application.Current == null)
        {
            if (Application.ResourceAssembly != typeof(Golan.Gdm.App).Assembly)
            {
                var resField = typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic);
                if (resField != null)
                    resField.SetValue(null, typeof(Golan.Gdm.App).Assembly);
                else
                    Application.ResourceAssembly = typeof(Golan.Gdm.App).Assembly;
            }

            // WPF's Application() constructor posts DoStartup (which calls App.OnStartup) via BeginInvoke.
            // Abort that operation immediately so App.OnStartup never runs (no tray, no pipe, no hidden MainWindow).
            void AbortStartupOp(object? s, DispatcherHookEventArgs e) => e.Operation.Abort();
            Dispatcher.CurrentDispatcher.Hooks.OperationPosted += AbortStartupOp;
            Golan.Gdm.App app;
            try
            {
                app = new Golan.Gdm.App
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };
            }
            finally
            {
                Dispatcher.CurrentDispatcher.Hooks.OperationPosted -= AbortStartupOp;
            }
            app.InitializeComponent();
        }

        if (SynchronizationContext.Current is not DispatcherSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        }

        Loc.Init("ar");
        Env = SampleDataSeeder.Seed(gdmHome);
        BindAppServices(Env);
        ReloadAppResources("ar", dark: false);
    }

    static void BindAppServices(SeededEnvironment env)
    {
        if (Application.Current is not Golan.Gdm.App appInstance) return;
        SetPrivateProperty(appInstance, nameof(Golan.Gdm.App.Settings), env.Settings);
        SetPrivateProperty(appInstance, nameof(Golan.Gdm.App.Manager), env.Manager);
        if (appInstance.Scheduler == null || !ReferenceEquals(appInstance.Manager, env.Manager))
            SetPrivateProperty(appInstance, nameof(Golan.Gdm.App.Scheduler), new QueueScheduler(env.Manager));
        if (appInstance.PowerGuard == null)
            SetPrivateProperty(appInstance, nameof(Golan.Gdm.App.PowerGuard), new PowerGuard());
        if (appInstance.SpeedRuleController == null)
            SetPrivateProperty(appInstance, nameof(Golan.Gdm.App.SpeedRuleController), new SpeedRuleController(env.Settings, env.Manager.Limiter, startTimer: false));
        if (appInstance.CliTarget == null)
            SetPrivateProperty(appInstance, nameof(Golan.Gdm.App.CliTarget), new DownloadManagerCliTarget(env.Manager));
    }

    static void ReloadAppResources(string lang, bool dark)
    {
        Loc.Init(lang);
        if (Application.Current is not Golan.Gdm.App app) return;

        var contentLoadedField = typeof(Golan.Gdm.App).GetField("_contentLoaded", BindingFlags.Instance | BindingFlags.NonPublic);
        contentLoadedField?.SetValue(app, false);
        app.Resources.Clear();
        app.InitializeComponent();

        // Apply theme while brushes in the newly loaded ResourceDictionary are still unfrozen
        Theme.Apply(dark);
        EnsureOffscreenWindowStyle();
    }

    static void EnsureOffscreenWindowStyle()
    {
        if (Application.Current?.Resources["GdmWindow"] is Style origStyle)
        {
            var newStyle = new Style(typeof(Window), origStyle);
            newStyle.Setters.Add(new Setter(Window.ShowActivatedProperty, false));
            newStyle.Setters.Add(new Setter(Window.ShowInTaskbarProperty, false));
            newStyle.Setters.Add(new Setter(Window.LeftProperty, -32000.0));
            newStyle.Setters.Add(new Setter(Window.TopProperty, -32000.0));
            Application.Current.Resources["GdmWindow"] = newStyle;
        }
    }

    public static void ApplyVariant(string lang, bool dark)
    {
        Loc.Init(lang);
        if (Env != null)
        {
            Env.Settings.Language = lang;
            Env.Settings.DarkMode = dark;
            Env.RefreshLocalizedStrings();
            BindAppServices(Env);
        }
        ReloadAppResources(lang, dark);
    }

    public static RenderedOutput RenderWindow(
        Window window,
        string windowKey,
        string lang,
        string theme,
        string outDir,
        Size? explicitSize = null,
        Action<Window>? afterLoaded = null)
    {
        Directory.CreateDirectory(outDir);

        PrepareOffscreenWindow(window, explicitSize);

        try
        {
            window.Show();
            PumpDispatcher(window.Dispatcher);

            afterLoaded?.Invoke(window);
            PumpDispatcher(window.Dispatcher);

            return CaptureAndInspect(window, windowKey, lang, theme, outDir, explicitSize);
        }
        finally
        {
            CloseCleanly(window);
        }
    }

    public static RenderedOutput RenderModalDialog(
        Action<Window> invokeModalWithDummyOwner,
        string windowKey,
        string lang,
        string theme,
        string outDir)
    {
        Directory.CreateDirectory(outDir);
        var dummyOwner = new Window
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 100,
            Height = 100,
        };

        RenderedOutput? captured = null;
        try
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                var modal = Application.Current?.Windows.OfType<Window>()
                    .LastOrDefault(w => !ReferenceEquals(w, dummyOwner) && w.IsVisible);
                if (modal != null)
                {
                    modal.Left = -32000;
                    modal.Top = -32000;
                    PumpDispatcher(modal.Dispatcher);
                    captured = CaptureAndInspect(modal, windowKey, lang, theme, outDir, null);
                    modal.Close();
                }
            }));

            invokeModalWithDummyOwner(dummyOwner);
            PumpDispatcher(Dispatcher.CurrentDispatcher);
        }
        finally
        {
            try { _ = new System.Windows.Interop.WindowInteropHelper(dummyOwner).EnsureHandle(); } catch { }
            CloseCleanly(dummyOwner);
        }

        return captured ?? throw new InvalidOperationException($"Modal dialog '{windowKey}' was not captured.");
    }

    public static void PrepareOffscreenWindow(Window window, Size? explicitSize = null)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;

        if (explicitSize is { } sz)
        {
            window.SizeToContent = SizeToContent.Manual;
            window.Width = sz.Width;
            window.Height = sz.Height;
        }

        if (window is MainWindow mw)
        {
            var closingMethod = typeof(MainWindow).GetMethod("MainWindow_Closing", BindingFlags.Instance | BindingFlags.NonPublic);
            if (closingMethod != null)
            {
                var del = (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), mw, closingMethod);
                mw.Closing -= del;
            }
            mw.Closing += static (_, e) => e.Cancel = false;
            var ignoreField = typeof(MainWindow).GetField("_ignoreClipboardUntil", BindingFlags.Instance | BindingFlags.NonPublic);
            ignoreField?.SetValue(mw, DateTime.MaxValue);
        }
    }

    public static RenderedOutput CaptureAndInspect(
        Window window,
        string windowKey,
        string lang,
        string theme,
        string outDir,
        Size? explicitSize = null)
    {
        window.Left = -32000;
        window.Top = -32000;
        window.UpdateLayout();

        var visualRoot = (VisualTreeHelper.GetChildrenCount(window) > 0
            ? VisualTreeHelper.GetChild(window, 0) as FrameworkElement
            : null) ?? (window.Content as FrameworkElement) ?? window;

        if (explicitSize is { } targetSize)
        {
            visualRoot.Measure(targetSize);
            visualRoot.Arrange(new Rect(0, 0, targetSize.Width, targetSize.Height));
            visualRoot.UpdateLayout();
        }
        else
        {
            visualRoot.UpdateLayout();
        }

        int pw = Math.Max(1, (int)Math.Ceiling(explicitSize?.Width ?? (visualRoot.ActualWidth > 0 ? visualRoot.ActualWidth : window.ActualWidth)));
        int ph = Math.Max(1, (int)Math.Ceiling(explicitSize?.Height ?? (visualRoot.ActualHeight > 0 ? visualRoot.ActualHeight : window.ActualHeight)));

        var rtb = new RenderTargetBitmap(pw, ph, 96.0, 96.0, PixelFormats.Pbgra32);
        if (window.Background != null && !window.AllowsTransparency)
        {
            var bgVisual = new DrawingVisual();
            using (var dc = bgVisual.RenderOpen())
                dc.DrawRectangle(window.Background, null, new Rect(0, 0, pw, ph));
            rtb.Render(bgVisual);
        }
        rtb.Render(visualRoot);

        string pngPath = Path.Combine(outDir, $"{windowKey}.{lang}.{theme}.png");
        bool needsRtlFlip = visualRoot.FlowDirection == FlowDirection.RightToLeft
            && VisualTreeHelper.GetTransform(visualRoot) == null;
        BitmapSource finalBitmap = needsRtlFlip
            ? new TransformedBitmap(rtb, new ScaleTransform(-1, 1))
            : rtb;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(finalBitmap));
        using (var fs = File.Create(pngPath))
            encoder.Save(fs);

        var issues = LayoutInspector.Inspect(visualRoot, windowKey, lang, theme);
        return new RenderedOutput(windowKey, $"{lang}.{theme}", lang, theme, pngPath, pw, ph, issues);
    }

    public static void CloseCleanly(Window window)
    {
        try
        {
            SampleDataSeeder.StopPrivateTimer(window, "_timer");
            SampleDataSeeder.StopPrivateTimer(window, "_uiTimer");
            SampleDataSeeder.StopPrivateTimer(window, "_statsTimer");
            window.Close();
            PumpDispatcher(window.Dispatcher);
        }
        catch
        {
        }
    }

    public static void PumpDispatcher(Dispatcher dispatcher)
    {
        dispatcher.Invoke( static () => { }, DispatcherPriority.ContextIdle);
    }

    static void SetPrivateProperty(object target, string propName, object? value)
    {
        var type = target.GetType();
        while (type != null)
        {
            var field = type.GetField($"<{propName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }
            var prop = type.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (prop?.GetSetMethod(nonPublic: true) is { } setter)
            {
                setter.Invoke(target, [value]);
                return;
            }
            type = type.BaseType;
        }
    }
}
