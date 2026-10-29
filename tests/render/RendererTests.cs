using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Golan.Gdm.Core;

namespace Golan.Gdm.Tests.Render;

public static class RendererTests
{
    public static (int Passed, int Failed) RunAll(string gdmHome)
    {
        int passed = 0, failed = 0;

        void AssertTrue(string name, bool condition, string detail = "")
        {
            if (condition)
            {
                Console.WriteLine($"PASS [TDD] {name}");
                passed++;
            }
            else
            {
                Console.WriteLine($"FAIL [TDD] {name} {detail}");
                failed++;
            }
        }

        // 1. Isolation check: AppPaths must be inside gdmHome and never %APPDATA%\Golan
        var realAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var realLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AssertTrue("AppPaths.DataDir is isolated under GDM_HOME",
            AppPaths.DataDir.StartsWith(gdmHome, StringComparison.OrdinalIgnoreCase)
            && !AppPaths.DataDir.StartsWith(Path.Combine(realAppData, "Golan"), StringComparison.OrdinalIgnoreCase));
        AssertTrue("AppPaths.LocalDir is isolated under GDM_HOME",
            AppPaths.LocalDir.StartsWith(gdmHome, StringComparison.OrdinalIgnoreCase)
            && !AppPaths.LocalDir.StartsWith(Path.Combine(realLocalAppData, "Golan"), StringComparison.OrdinalIgnoreCase));

        // 2. App initialization & variant switching
        try
        {
            WindowRenderer.InitializeApp(gdmHome);
            AssertTrue("Application.Current is initialized", Application.Current is Golan.Gdm.App && Golan.Gdm.App.Instance.Settings != null && Golan.Gdm.App.Instance.Manager != null);

            WindowRenderer.ApplyVariant("ar", dark: true);
            var darkBg = (Application.Current!.Resources["WindowBg"] as SolidColorBrush)?.Color;
            AssertTrue("ApplyVariant('ar', dark: true) sets RTL and dark WindowBg (#0E0E10)",
                Loc.IsRtl && darkBg == Color.FromRgb(0x0E, 0x0E, 0x10), $"IsRtl={Loc.IsRtl}, bg={darkBg}");

            WindowRenderer.ApplyVariant("en", dark: false);
            var lightBg = (Application.Current!.Resources["WindowBg"] as SolidColorBrush)?.Color;
            AssertTrue("ApplyVariant('en', dark: false) sets LTR and light WindowBg (#F5F5F3)",
                !Loc.IsRtl && lightBg == Color.FromRgb(0xF5, 0xF5, 0xF3), $"IsRtl={Loc.IsRtl}, bg={lightBg}");

            // 2b. Golan Brand Palette & WCAG Contrast Token Verification
            var brandYellow = (Application.Current!.Resources["Accent"] as SolidColorBrush)?.Color;
            var brandInk = (Application.Current!.Resources["AccentInk"] as SolidColorBrush)?.Color;
            var lightAccentText = (Application.Current!.Resources["AccentText"] as SolidColorBrush)?.Color;
            var lightSurface = (Application.Current!.Resources["Surface"] as SolidColorBrush)?.Color;
            var lightAccentSoft = (Application.Current!.Resources["AccentSoft"] as SolidColorBrush)?.Color;
            AssertTrue("Golan Brand Accent is #FDD103 and AccentInk is #131313",
                brandYellow == Color.FromRgb(0xFD, 0xD1, 0x03) && brandInk == Color.FromRgb(0x13, 0x13, 0x13),
                $"Accent={brandYellow}, AccentInk={brandInk}");
            AssertTrue("Golan Primary button contrast (AccentInk #131313 on Accent #FDD103) >= 10:1",
                brandYellow.HasValue && brandInk.HasValue && LayoutInspector.ContrastRatio(brandInk.Value, brandYellow.Value) >= 10.0,
                $"ratio={(brandYellow.HasValue && brandInk.HasValue ? LayoutInspector.ContrastRatio(brandInk.Value, brandYellow.Value) : 0):F2}");
            AssertTrue("Golan Light AccentText has WCAG AA contrast >= 4.5:1 on Surface and AccentSoft",
                lightAccentText.HasValue && lightSurface.HasValue && lightAccentSoft.HasValue
                && LayoutInspector.ContrastRatio(lightAccentText.Value, lightSurface.Value) >= 4.5
                && LayoutInspector.ContrastRatio(lightAccentText.Value, lightAccentSoft.Value) >= 4.5,
                $"AccentText={lightAccentText}");

            WindowRenderer.ApplyVariant("en", dark: true);
            var darkAccentText = (Application.Current!.Resources["AccentText"] as SolidColorBrush)?.Color;
            var darkSurface = (Application.Current!.Resources["Surface"] as SolidColorBrush)?.Color;
            var darkAccentSoft = (Application.Current!.Resources["AccentSoft"] as SolidColorBrush)?.Color;
            AssertTrue("Golan Dark AccentText has WCAG AA contrast >= 4.5:1 on Dark Surface and AccentSoft",
                darkAccentText.HasValue && darkSurface.HasValue && darkAccentSoft.HasValue
                && LayoutInspector.ContrastRatio(darkAccentText.Value, darkSurface.Value) >= 4.5
                && LayoutInspector.ContrastRatio(darkAccentText.Value, darkAccentSoft.Value) >= 4.5,
                $"DarkAccentText={darkAccentText}");

            // 3. Offscreen PNG rendering
            var tempOut = Path.Combine(gdmHome, "test-out");
            var sampleWin = new Window
            {
                Title = "Sample",
                Width = 320,
                Height = 180,
                Background = Brushes.White,
                FlowDirection = FlowDirection.LeftToRight,
                Content = new TextBlock { Text = "Hello test", Foreground = Brushes.Black, Margin = new Thickness(12) },
            };
            var rendered = WindowRenderer.RenderWindow(sampleWin, "SampleTest", "en", "light", tempOut, new Size(320, 180));
            AssertTrue("RenderWindow writes non-empty PNG at expected dimensions",
                File.Exists(rendered.PngPath) && new FileInfo(rendered.PngPath).Length > 100 && rendered.PixelWidth == 320 && rendered.PixelHeight == 180,
                $"w={rendered.PixelWidth}, h={rendered.PixelHeight}");

            // 4. Switching to en.dark updates GdmWindow style FlowDirection and DataGrid dark background, and populates MainWindow items
            WindowRenderer.ApplyVariant("en", dark: true);
            AssertTrue("App.OnStartup did not run (MainView is null)", Golan.Gdm.App.Instance.MainView == null);
            var mw = new Golan.Gdm.UI.MainWindow();
            var mainOut = WindowRenderer.RenderWindow(mw, "MainTddTest", "en", "dark", tempOut, new Size(1180, 680), afterLoaded: w =>
            {
                var grid = (DataGrid)w.FindName("Grid")!;
                var emptyState = (FrameworkElement)w.FindName("EmptyState")!;
                var toolbarBorder = (Border)w.FindName("ToolbarBorder")!;
                var toolbarBg = (toolbarBorder.Background as SolidColorBrush)?.Color;
                AssertTrue("MainWindow in en.dark has LeftToRight FlowDirection from GdmWindow style",
                    w.FlowDirection == FlowDirection.LeftToRight, $"actual={w.FlowDirection}");
                AssertTrue("MainWindow ToolbarBorder in en.dark has dark Surface background (#161619)",
                    toolbarBg == Color.FromRgb(0x16, 0x16, 0x19), $"actual={toolbarBg}");
                AssertTrue("MainWindow DataGrid has 7 seeded items and EmptyState is Collapsed",
                    grid.Items.Count == 7 && emptyState.Visibility == Visibility.Collapsed,
                    $"items={grid.Items.Count}, emptyVis={emptyState.Visibility}");
            });

            // 5. RenderModalDialog captures modal dialog offscreen and leaves 0 unclosed windows
            var modalOut = WindowRenderer.RenderModalDialog(
                owner => Golan.Gdm.UI.Confirm.Ask(owner, "Confirm Title", "Confirm Body", "OK", danger: true),
                "ModalTddTest", "en", "light", tempOut);
            var leftoverList = Application.Current!.Windows.OfType<Window>()
                .Select(w => $"{w.GetType().Name}('{w.Title}', vis={w.IsVisible})")
                .ToList();
            AssertTrue("RenderModalDialog writes valid PNG and leaves 0 unclosed windows",
                File.Exists(modalOut.PngPath) && modalOut.PixelWidth > 100 && modalOut.PixelHeight > 80 && leftoverList.Count == 0,
                $"w={modalOut.PixelWidth}, h={modalOut.PixelHeight}, leftover=[{string.Join(", ", leftoverList)}]");

            // 6. RenderWindow with explicitSize taller than primary monitor (e.g., 640x1280) arranges content to full height without WM_GETMINMAXINFO clamping
            var tallGrid = new Grid();
            var tallWin = new Window
            {
                Title = "TallWindow",
                Width = 640,
                Height = 680,
                Content = tallGrid,
            };
            WindowRenderer.RenderWindow(tallWin, "TallTddTest", "en", "light", tempOut, new Size(640, 1280));
            AssertTrue("RenderWindow with explicitSize(640, 1280) arranges content to full 1280px height",
                Math.Abs(tallGrid.ActualHeight - 1280) < 2.0,
                $"tallGrid.ActualHeight={tallGrid.ActualHeight}");

            // 7. Zero hardcoded colors outside token dictionaries (UI/Theme/*.xaml and Theme class)
            var gdmRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var srcGdm = Path.Combine(gdmRoot, "src", "GDM");
            var themeDir = Path.Combine(srcGdm, "UI", "Theme");
            AssertTrue("Token dictionaries Brand.xaml, Light.xaml, Dark.xaml exist in UI/Theme",
                File.Exists(Path.Combine(themeDir, "Brand.xaml"))
                && File.Exists(Path.Combine(themeDir, "Light.xaml"))
                && File.Exists(Path.Combine(themeDir, "Dark.xaml")));

            var strayColors = ScanForStrayColors(srcGdm);
            AssertTrue("Zero stray hardcoded hex/RGB colors outside UI/Theme token files",
                strayColors.Count == 0,
                strayColors.Count > 0 ? string.Join(" | ", strayColors.Take(8)) : "");

            // 8. Multi-size gdm.ico (16, 20, 24, 32, 40, 48, 64, 96, 128, 256) and clean brand assets exist
            var icoPath = Path.Combine(srcGdm, "Assets", "gdm.ico");
            var requiredSizes = new[] { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
            var icoSizes = ReadIcoFrameSizes(icoPath);
            AssertTrue("Assets/gdm.ico contains all 10 required sizes (16..256)",
                requiredSizes.All(s => icoSizes.Contains(s)),
                $"found=[{string.Join(",", icoSizes)}]");

            // 9. Extension, remote.html, store, and installer assets use Golan brand and no old blue (#2563EB/#3B82F6)
            var remoteHtml = File.ReadAllText(Path.Combine(srcGdm, "Integration", "Remote", "remote.html"));
            var popupCss = File.ReadAllText(Path.Combine(gdmRoot, "extension", "popup.css"));
            var contentJs = File.ReadAllText(Path.Combine(gdmRoot, "extension", "content.js"));
            var gdmIss = File.ReadAllText(Path.Combine(gdmRoot, "installer", "gdm.iss"));
            bool noOldBlueInWeb = !remoteHtml.Contains("#2563eb", StringComparison.OrdinalIgnoreCase)
                && !remoteHtml.Contains("#3b82f6", StringComparison.OrdinalIgnoreCase)
                && !popupCss.Contains("#2563eb", StringComparison.OrdinalIgnoreCase)
                && !popupCss.Contains("#3b82f6", StringComparison.OrdinalIgnoreCase)
                && !contentJs.Contains("#2563eb", StringComparison.OrdinalIgnoreCase)
                && remoteHtml.Contains("#FDD103", StringComparison.OrdinalIgnoreCase)
                && popupCss.Contains("#FDD103", StringComparison.OrdinalIgnoreCase)
                && contentJs.Contains("#FDD103", StringComparison.OrdinalIgnoreCase);
            AssertTrue("remote.html, extension/popup.css, and extension/content.js use Golan #FDD103 and no old blue",
                noOldBlueInWeb);

            bool installerBranded = gdmIss.Contains("gdm.ico", StringComparison.OrdinalIgnoreCase)
                && gdmIss.Contains("WizardImageFile", StringComparison.OrdinalIgnoreCase)
                && gdmIss.Contains("WizardSmallImageFile", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(gdmRoot, "installer", "wizard-large-100.bmp"))
                && File.Exists(Path.Combine(gdmRoot, "installer", "wizard-small-100.bmp"));
            AssertTrue("Installer gdm.iss and wizard BMP assets exist and are branded", installerBranded);

            bool storeAssetsExist = File.Exists(Path.Combine(gdmRoot, "store", "promo-small-440x280.png"))
                && File.Exists(Path.Combine(gdmRoot, "store", "promo-marquee-1400x560.png"))
                && File.Exists(Path.Combine(gdmRoot, "store", "icon-300.png"))
                && File.Exists(Path.Combine(gdmRoot, "extension", "icons", "icon128.png"));
            AssertTrue("Store promo tiles and extension icons exist", storeAssetsExist);

            // 10. 16x16 small icon frame uses a dedicated full-height glyph (>= 9 vertical rows of dark ink) and App.xaml.cs CreateTray uses SmallIconSize
            int icon16DarkRows = CountDarkInkRowsInPng(Path.Combine(gdmRoot, "extension", "icons", "icon16.png"));
            var appXamlCs = File.ReadAllText(Path.Combine(srcGdm, "App.xaml.cs"));
            AssertTrue("16x16 icon uses dedicated full-height glyph (>= 9 dark ink rows) and CreateTray selects SmallIconSize",
                icon16DarkRows >= 9 && appXamlCs.Contains("SmallIconSize", StringComparison.Ordinal),
                $"icon16DarkRows={icon16DarkRows}, hasSmallIconSize={appXamlCs.Contains("SmallIconSize", StringComparison.Ordinal)}");

            // 11. Main window at 1366x768 fits all 13 toolbar buttons on a single row in both Arabic and English with 0 layout issues
            WindowRenderer.ApplyVariant("ar", dark: false);
            var welcomeArOut = WindowRenderer.RenderWindow(new Golan.Gdm.UI.WelcomeWindow(), "Welcome_ar", "ar", "light", tempOut);
            double welcomeYellowX = MeanBrandYellowXInTopBar(welcomeArOut.PngPath, 140);
            AssertTrue("WelcomeWindow in Arabic renders RTL with brand badge on the right (un-mirrored)",
                welcomeYellowX > welcomeArOut.PixelWidth * 0.70,
                $"welcomeYellowX={welcomeYellowX:F1}, width={welcomeArOut.PixelWidth}");

            foreach (var lang1366 in new[] { "ar", "en" })
            foreach (var dark1366 in new[] { false, true })
            {
                string theme1366 = dark1366 ? "dark" : "light";
                WindowRenderer.ApplyVariant(lang1366, dark: dark1366);
                var mw1366 = new Golan.Gdm.UI.MainWindow();
                int buttonCount = 0;
                double ySpan = 999;
                double toolbarH = 999;
                var out1366 = WindowRenderer.RenderWindow(mw1366, $"Main1366_{lang1366}_{theme1366}", lang1366, theme1366, tempOut, new Size(1366, 768), afterLoaded: w =>
                {
                    var toolbar = (WrapPanel)w.FindName("Toolbar")!;
                    var buttons = new List<FrameworkElement>();
                    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(toolbar); i++)
                    {
                        if (VisualTreeHelper.GetChild(toolbar, i) is System.Windows.Controls.Primitives.ButtonBase btn && btn.Visibility == Visibility.Visible)
                            buttons.Add(btn);
                    }
                    var tops = buttons.Select(b => b.TranslatePoint(new Point(0, 0), toolbar).Y).ToList();
                    buttonCount = buttons.Count;
                    ySpan = tops.Count > 0 ? tops.Max() - tops.Min() : 999;
                    toolbarH = toolbar.ActualHeight;
                });
                double mainYellowX = MeanBrandYellowXInTopBar(out1366.PngPath, 100);
                bool validLogoSide = lang1366 == "ar" ? mainYellowX > 1050 : mainYellowX < 320;
                AssertTrue($"Main_1366x768 ({lang1366}/{theme1366}) fits all 13 toolbar buttons on a single row with 0 layout issues and un-mirrored logo",
                    buttonCount == 13 && ySpan < 4.0 && toolbarH < 58.0 && out1366.Issues.Count == 0 && validLogoSide,
                    $"buttons={buttonCount}, ySpan={ySpan:F1}, toolbarH={toolbarH:F1}, issues={out1366.Issues.Count}, logoX={mainYellowX:F1}");
            }

            // 12. Seeded sample environment uses Golan filenames
            AssertTrue("Seeded sample data uses golan-sdk filename",
                WindowRenderer.Env != null
                && WindowRenderer.Env.CompletedZipItem.FileName.Contains("golan-sdk", StringComparison.OrdinalIgnoreCase)
                && !WindowRenderer.Env.CompletedZipItem.FileName.Contains("\u0061dam", StringComparison.OrdinalIgnoreCase),
                $"fileName={WindowRenderer.Env?.CompletedZipItem.FileName}");
        }
        catch (Exception ex)
        {
            AssertTrue("WindowRenderer initialization and offscreen capture", false, ex.Message);
        }

        return (passed, failed);
    }

    static List<string> ScanForStrayColors(string srcGdm)
    {
        var hexRegex = new System.Text.RegularExpressions.Regex(@"(?<!&)#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\b");
        var rgbRegex = new System.Text.RegularExpressions.Regex(@"Color\.FromRgb\s*\(");
        var namedUiColorRegex = new System.Text.RegularExpressions.Regex(@"\b(?:Foreground|Background|Stroke|Fill|BorderBrush)\s*=\s*""(?:White|Black|Red|Green|Blue|Orange|Yellow|Gray)""");
        var hits = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcGdm, "*.*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var rel = Path.GetRelativePath(srcGdm, file).Replace('/', '\\');
            if (rel.StartsWith("obj\\", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("bin\\", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("UI\\Theme\\", StringComparison.OrdinalIgnoreCase))
                continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("<!--"))
                    continue;

                if (hexRegex.IsMatch(line) || rgbRegex.IsMatch(line) || (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && namedUiColorRegex.IsMatch(line)))
                {
                    hits.Add($"{rel}:{i + 1}");
                }
            }
        }
        return hits;
    }

    static List<int> ReadIcoFrameSizes(string icoPath)
    {
        var sizes = new List<int>();
        if (!File.Exists(icoPath)) return sizes;
        using var fs = File.OpenRead(icoPath);
        using var br = new BinaryReader(fs);
        if (fs.Length < 6) return sizes;
        ushort reserved = br.ReadUInt16();
        ushort type = br.ReadUInt16();
        ushort count = br.ReadUInt16();
        if (reserved != 0 || type != 1) return sizes;
        for (int i = 0; i < count; i++)
        {
            if (fs.Position + 16 > fs.Length) break;
            byte w = br.ReadByte();
            byte h = br.ReadByte();
            fs.Seek(14, SeekOrigin.Current);
            int width = w == 0 ? 256 : w;
            int height = h == 0 ? 256 : h;
            if (width == height && !sizes.Contains(width))
                sizes.Add(width);
        }
        sizes.Sort();
        return sizes;
    }

    static int CountDarkInkRowsInPng(string pngPath)
    {
        if (!File.Exists(pngPath)) return 0;
        var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
            new Uri(pngPath),
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth;
        int h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        int darkRows = 0;
        for (int y = 0; y < h; y++)
        {
            bool rowHasDark = false;
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                byte b = pixels[idx];
                byte g = pixels[idx + 1];
                byte r = pixels[idx + 2];
                byte a = pixels[idx + 3];
                if (a > 200 && r < 80 && g < 80 && b < 80)
                {
                    rowHasDark = true;
                    break;
                }
            }
            if (rowHasDark) darkRows++;
        }
        return darkRows;
    }

    static double MeanBrandYellowXInTopBar(string pngPath, int maxY)
    {
        if (!File.Exists(pngPath)) return -1;
        var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
            new Uri(pngPath),
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var bgra = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth;
        int h = Math.Min(bgra.PixelHeight, maxY);
        var pixels = new byte[w * bgra.PixelHeight * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        long sumX = 0, count = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                byte b = pixels[idx];
                byte g = pixels[idx + 1];
                byte r = pixels[idx + 2];
                if (r > 245 && g > 195 && b < 15)
                {
                    sumX += x;
                    count++;
                }
            }
        }
        return count > 0 ? (double)sumX / count : -1;
    }
}
