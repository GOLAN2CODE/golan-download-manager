using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Golan.Gdm.Tests.Render;

public enum IssueKind
{
    TextClipped,
    OverflowBounds,
    OverlappingSiblings,
    UntranslatedKey,
    LanguageMismatch,
    WrongFlowDirection,
    LowContrast,
}

public sealed record LayoutIssue(
    string WindowName,
    string Variant,
    IssueKind Kind,
    string ElementPath,
    string ElementType,
    string ElementName,
    string Detail,
    Rect Bounds);

public static class LayoutInspector
{
    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    static readonly Regex KeyPattern = new(
        @"^(?:main|win|core|cat|col|status|dlg|opt|err|msg|menu|tab|btn|lbl|tray|remote|torrent|video|idm|hash|stats|speed|sched|batch|grab)\.[a-zA-Z0-9_.\-]+$|^[a-z]{2,10}\.[a-zA-Z0-9_]+\.[a-zA-Z0-9_.\-]+$",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        RegexTimeout);

    static readonly Regex ArabicCharRegex = new(
        @"[\u0600-\u06FF\u0750-\u077F\u08A0-\u08FF]",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        RegexTimeout);

    static readonly Regex UrlOrPathRegex = new(
        @"https?://\S+|magnet:\?\S+|\S+@\S+|[A-Za-z]:\\[^\s""']+|\\\\[^\s""']+|\b[\w.\-]+(?:/[\w.\-]+)+|\*\.[a-zA-Z0-9.\-]+|\b[a-zA-Z0-9.\-]+\.(?:com|org|net|io|dev|edu|gov|local)(?:/\S*)?|\b[\w.\-]+\.(?:zip|iso|exe|msi|mp4|mkv|webm|mp3|m4a|aac|flac|wav|torrent|pdf|doc|docx|xls|xlsx|ppt|pptx|json|txt|csv|m3u|m3u8|7z|rar|tar|gz|xz|bin|srt|vtt|ass|xpi|crx|part|gdm|dll|md)\b|\.[a-zA-Z0-9]{2,5}\b|\[FILE\]|\b[0-9a-fA-F]{16,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.NonBacktracking,
        RegexTimeout);

    static readonly Regex LatinWordRegex = new(
        @"\b[A-Za-z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.NonBacktracking,
        RegexTimeout);

    static readonly HashSet<string> AllowedTechnicalWordsInArabic = new(StringComparer.OrdinalIgnoreCase)
    {
        "HTTP", "HTTPS", "SOCKS", "SOCKS5", "FTP", "SFTP",
        "SHA", "SHA1", "SHA256", "SHA512", "MD5", "CRC32",
        "IDM", "URL", "URLs", "URI", "PIN", "QR", "JSON", "CSV", "M3U", "M3U8", "HLS", "DASH",
        "ZIP", "RAR", "TAR", "GZ", "XZ", "BZ2", "ISO", "IMG", "DMG",
        "MP3", "MP4", "MKV", "WEBM", "M4A", "AAC", "FLAC", "WAV", "OGG", "OPUS", "AVI", "MOV", "FLV", "WMV",
        "EXE", "MSI", "BAT", "CMD", "PS1", "APK", "DEB", "RPM", "DLL", "XPI", "CRX",
        "PDF", "DOC", "DOCX", "XLS", "XLSX", "PPT", "PPTX", "TXT", "RTF", "EPUB", "SRT", "VTT", "ASS", "JPG", "JPEG", "PNG", "WEBP",
        "TORRENT", "MAGNET", "INFOHASH",
        "KB", "MB", "GB", "TB", "PB", "KiB", "MiB", "GiB", "kbps", "Mbps", "Gbps", "ms", "sec", "min", "hr", "px", "fps", "Hz", "kHz", "HD", "Full", "Audio",
        "yt", "dlp", "ffmpeg", "ffprobe", "aria2", "MonoTorrent",
        "BitTorrent", "DHT", "PEX", "LSD", "UPnP", "NAT", "PMP", "uTP", "TCP", "UDP", "WebSeed",
        "Chrome", "Chromium", "Edge", "Brave", "Vivaldi", "Opera", "Firefox", "Safari",
        "Windows", "Defender", "SmartScreen", "Microsoft", "Linux", "Ubuntu", "Debian", "DVD", "Android", "iOS", "macOS",
        "Alt", "Ctrl", "Shift", "Esc", "Enter", "Tab", "Backspace", "Del", "Ins",
        "LAN", "WAN", "Wi", "Fi", "WiFi", "IP", "IPv4", "IPv6", "DNS", "API", "TLS", "SSL", "SSH",
        "User", "Agent", "Referer", "Referrer", "Cookie", "Cookies", "ETag", "Range", "Host", "Header", "Headers", "Basic", "Bearer",
        "GDM", "Golan", "English", "Desktop", "amd64", "x64", "x86", "arm64", "v1", "v2", "v3", "v4", "ar", "en", "auto", "ci", "bot", "Pixel", "Pro", "iPad", "Air",
        "AM", "PM", "UTC", "GMT", "HTML", "XML", "SQL", "DB", "UI", "DPI", "GPU", "CPU", "RAM", "SSD", "HDD", "WPF", "NET", "DotNet",
    };

    [ThreadStatic]
    static TextBlock? _measureProbe;

    static TextBlock GetMeasureProbe() => _measureProbe ??= new TextBlock();

    public static List<LayoutIssue> Inspect(FrameworkElement root, string windowName, string lang, string theme)
    {
        var issues = new List<LayoutIssue>();
        string variant = $"{lang}.{theme}";
        double rootW = root.ActualWidth;
        double rootH = root.ActualHeight;
        if (rootW <= 0 || rootH <= 0) return issues;

        var expectedFlow = lang == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (root.FlowDirection != expectedFlow)
        {
            issues.Add(new LayoutIssue(
                windowName, variant, IssueKind.WrongFlowDirection,
                GetPath(root, root), root.GetType().Name, root.Name ?? "",
                $"Root FlowDirection is {root.FlowDirection}, expected {expectedFlow} for lang='{lang}'",
                new Rect(0, 0, rootW, rootH)));
        }

        WalkElement(root, root, windowName, variant, lang, rootW, rootH, issues);
        return issues;
    }

    static void WalkElement(
        DependencyObject current,
        FrameworkElement root,
        string windowName,
        string variant,
        string lang,
        double rootW,
        double rootH,
        List<LayoutIssue> issues)
    {
        if (current is UIElement uie && uie.Visibility != Visibility.Visible)
            return;
        if (current is FrameworkElement feCurr && (feCurr.Opacity <= 0.01 || feCurr.ActualWidth <= 0 || feCurr.ActualHeight <= 0))
            return;

        if (current is FrameworkElement fe)
        {
            string path = GetPath(fe, root);
            Rect bounds = GetBoundsInRoot(fe, root);

            // Check explicit wrong FlowDirection on child elements
            if (!ReferenceEquals(fe, root))
            {
                var vs = DependencyPropertyHelper.GetValueSource(fe, FrameworkElement.FlowDirectionProperty);
                if (vs.BaseValueSource is not BaseValueSource.Inherited and not BaseValueSource.Default)
                {
                    if (lang == "en" && fe.FlowDirection == FlowDirection.RightToLeft)
                    {
                        issues.Add(new LayoutIssue(
                            windowName, variant, IssueKind.WrongFlowDirection,
                            path, fe.GetType().Name, fe.Name ?? "",
                            $"Explicit FlowDirection=RightToLeft in English mode",
                            bounds));
                    }
                    else if (lang == "ar" && fe.FlowDirection == FlowDirection.LeftToRight && fe is TextBlock tbFlow && ArabicCharRegex.IsMatch(tbFlow.Text ?? ""))
                    {
                        issues.Add(new LayoutIssue(
                            windowName, variant, IssueKind.WrongFlowDirection,
                            path, fe.GetType().Name, fe.Name ?? "",
                            $"TextBlock with Arabic text has explicit FlowDirection=LeftToRight: \"{Truncate(tbFlow.Text, 40)}\"",
                            bounds));
                    }
                }
            }

            // Check OverflowBounds on leaf/interactive elements not inside a scrollable container
            if (IsLeafOrInteractive(fe) && !IsInsideScrollableArea(fe, root))
            {
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    if (bounds.Left < -2.5 || bounds.Top < -2.5 || bounds.Right > rootW + 2.5 || bounds.Bottom > rootH + 2.5)
                    {
                        issues.Add(new LayoutIssue(
                            windowName, variant, IssueKind.OverflowBounds,
                            path, fe.GetType().Name, fe.Name ?? "",
                            $"Element bounds ({bounds.Left:0.#},{bounds.Top:0.#} -> {bounds.Right:0.#},{bounds.Bottom:0.#}) overflow window (0,0 -> {rootW:0.#},{rootH:0.#})",
                            bounds));
                    }
                }
            }

            // Check TextBlock specific checks: TextClipped, UntranslatedKey, LanguageMismatch, LowContrast
            if (fe is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            {
                InspectTextBlock(tb, root, windowName, variant, lang, path, bounds, issues);
            }

            // Check ComboBox for broken DisplayMemberPath / blank selection binding
            if (fe is ComboBox cb && cb.Items.Count > 0 && !string.IsNullOrEmpty(cb.DisplayMemberPath))
            {
                var firstItem = cb.Items[0];
                if (firstItem != null && firstItem.GetType().GetProperty(cb.DisplayMemberPath) == null)
                {
                    issues.Add(new LayoutIssue(
                        windowName, variant, IssueKind.TextClipped,
                        path, "ComboBox", cb.Name ?? "",
                        $"ComboBox DisplayMemberPath='{cb.DisplayMemberPath}' does not match any public property on '{firstItem.GetType().Name}' (renders blank)",
                        bounds));
                }
            }

            // Check Panel siblings for overlap
            if (fe is Panel panel)
            {
                InspectPanelSiblings(panel, root, windowName, variant, issues);
            }
        }

        int count = VisualTreeHelper.GetChildrenCount(current);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(current, i);
            if (child != null)
                WalkElement(child, root, windowName, variant, lang, rootW, rootH, issues);
        }
    }

    static void InspectTextBlock(
        TextBlock tb,
        FrameworkElement root,
        string windowName,
        string variant,
        string lang,
        string path,
        Rect bounds,
        List<LayoutIssue> issues)
    {
        string text = tb.Text.Trim();
        if (text.Length == 0) return;

        bool isIconFont = tb.FontFamily?.Source?.Contains("Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase) == true
            || tb.FontFamily?.Source?.Contains("Segoe MDL2 Assets", StringComparison.OrdinalIgnoreCase) == true
            || (text.Length <= 2 && text[0] >= '\uE000' && text[0] <= '\uF8FF');

        // 1. TextClipped check using WPF's own TextBlock layout engine + LayoutInformation.GetLayoutClip
        if (!isIconFont && tb.ActualWidth > 1 && tb.ActualHeight > 1)
        {
            var probe = GetMeasureProbe();
            probe.Text = tb.Text;
            probe.FontFamily = tb.FontFamily ?? new FontFamily("Segoe UI");
            probe.FontSize = tb.FontSize > 0 ? tb.FontSize : 12.0;
            probe.FontStyle = tb.FontStyle;
            probe.FontWeight = tb.FontWeight;
            probe.FontStretch = tb.FontStretch;
            probe.FlowDirection = tb.FlowDirection;
            probe.Padding = tb.Padding;
            probe.TextTrimming = TextTrimming.None;
            TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(tb));

            double visibleW = tb.ActualWidth;
            double visibleH = tb.ActualHeight;
            if (LayoutInformation.GetLayoutClip(tb) is { } selfClip && !selfClip.Bounds.IsEmpty)
            {
                visibleW = Math.Min(visibleW, selfClip.Bounds.Width);
                visibleH = Math.Min(visibleH, selfClip.Bounds.Height);
            }
            if (VisualTreeHelper.GetParent(tb) is FrameworkElement parentFe
                && LayoutInformation.GetLayoutClip(parentFe) is { } parentClip
                && !parentClip.Bounds.IsEmpty)
            {
                visibleW = Math.Min(visibleW, parentClip.Bounds.Width);
                visibleH = Math.Min(visibleH, parentClip.Bounds.Height);
            }

            if (tb.TextWrapping == TextWrapping.NoWrap)
            {
                probe.TextWrapping = TextWrapping.NoWrap;
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double neededW = probe.DesiredSize.Width;
                if (neededW > visibleW + 1.5)
                {
                    issues.Add(new LayoutIssue(
                        windowName, variant, IssueKind.TextClipped,
                        path, "TextBlock", tb.Name ?? "",
                        $"Text horizontal overflow/clip: needs {neededW:F1}px, visible {visibleW:F1}px (Trimming={tb.TextTrimming}): \"{Truncate(text, 50)}\"",
                        bounds));
                }
            }
            else
            {
                probe.TextWrapping = tb.TextWrapping;
                probe.Measure(new Size(Math.Max(1.0, visibleW + 0.5), double.PositiveInfinity));
                double neededH = probe.DesiredSize.Height;
                if (neededH > visibleH + 2.0)
                {
                    issues.Add(new LayoutIssue(
                        windowName, variant, IssueKind.TextClipped,
                        path, "TextBlock", tb.Name ?? "",
                        $"Wrapped text vertical overflow/clip: needs {neededH:F1}px height, visible {visibleH:F1}px: \"{Truncate(text, 50)}\"",
                        bounds));
                }
            }
        }

        // 2. UntranslatedKey check
        if (!isIconFont && IsUntranslatedKey(text))
        {
            issues.Add(new LayoutIssue(
                windowName, variant, IssueKind.UntranslatedKey,
                path, "TextBlock", tb.Name ?? "",
                $"Untranslated key visible: \"{text}\"",
                bounds));
        }

        // 3. LanguageMismatch check
        if (!isIconFont && !IsInsideEditableTextInput(tb) && !IsUserDataBoundTextBlock(tb))
        {
            if (lang == "en")
            {
                if (ArabicCharRegex.IsMatch(text) && !text.StartsWith("العربية", StringComparison.Ordinal))
                {
                    issues.Add(new LayoutIssue(
                        windowName, variant, IssueKind.LanguageMismatch,
                        path, "TextBlock", tb.Name ?? "",
                        $"Arabic text visible in English mode: \"{Truncate(text, 60)}\"",
                        bounds));
                }
            }
            else if (lang == "ar")
            {
                var badWords = FindUnallowedEnglishWordsInArabic(text);
                if (badWords.Count > 0)
                {
                    issues.Add(new LayoutIssue(
                        windowName, variant, IssueKind.LanguageMismatch,
                        path, "TextBlock", tb.Name ?? "",
                        $"English word(s) [{string.Join(", ", badWords)}] visible in Arabic mode: \"{Truncate(text, 60)}\"",
                        bounds));
                }
            }
        }

        // 4. LowContrast check
        if (TryGetEffectiveColor(tb.Foreground, out Color fgColor) && TryFindAncestorBackgroundColor(tb, out Color bgColor))
        {
            Color blendedFg = CompositeOver(fgColor, bgColor);
            double ratio = ContrastRatio(blendedFg, bgColor);
            if (ratio < 2.8)
            {
                issues.Add(new LayoutIssue(
                    windowName, variant, IssueKind.LowContrast,
                    path, "TextBlock", tb.Name ?? "",
                    $"Low contrast ratio {ratio:0.00}:1 (fg=#{blendedFg.R:X2}{blendedFg.G:X2}{blendedFg.B:X2} on bg=#{bgColor.R:X2}{bgColor.G:X2}{bgColor.B:X2}): \"{Truncate(text, 45)}\"",
                    bounds));
            }
        }
    }

    static void InspectPanelSiblings(
        Panel panel,
        FrameworkElement root,
        string windowName,
        string variant,
        List<LayoutIssue> issues)
    {
        if (panel.TemplatedParent is Control)
            return;

        var children = new List<(FrameworkElement El, Rect Bounds)>();
        int count = VisualTreeHelper.GetChildrenCount(panel);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(panel, i) is FrameworkElement child
                && child.Visibility == Visibility.Visible
                && child.Opacity > 0.05
                && child.ActualWidth > 1
                && child.ActualHeight > 1)
            {
                if (IsPureBackgroundDecoration(child)) continue;
                children.Add((child, GetBoundsInRoot(child, root)));
            }
        }

        for (int i = 0; i < children.Count; i++)
        {
            for (int j = i + 1; j < children.Count; j++)
            {
                var (a, ra) = children[i];
                var (b, rb) = children[j];

                // If inside a Grid, if one spans across cells as a container and the other doesn't, check actual overlap
                Rect inter = Rect.Intersect(ra, rb);
                if (!inter.IsEmpty && inter.Width > 3.0 && inter.Height > 3.0)
                {
                    issues.Add(new LayoutIssue(
                        windowName, variant, IssueKind.OverlappingSiblings,
                        GetPath(panel, root), panel.GetType().Name, panel.Name ?? "",
                        $"Siblings overlap by {inter.Width:F1}x{inter.Height:F1}px: {DescribeElement(a)} and {DescribeElement(b)}",
                        inter));
                }
            }
        }
    }

    static bool IsPureBackgroundDecoration(FrameworkElement el)
    {
        if (el is System.Windows.Shapes.Shape) return true;
        if (el is Border b && b.Child == null) return true;
        return false;
    }

    static bool IsLeafOrInteractive(FrameworkElement fe) =>
        fe is ButtonBase or TextBox or PasswordBox or ComboBox or ProgressBar or Slider or TextBlock;

    static bool IsInsideScrollableArea(DependencyObject el, DependencyObject root)
    {
        var curr = VisualTreeHelper.GetParent(el);
        while (curr != null && !ReferenceEquals(curr, root))
        {
            if (curr is ScrollContentPresenter or ScrollViewer or VirtualizingStackPanel or DataGridCellsPresenter or DataGridColumnHeadersPresenter)
                return true;
            curr = VisualTreeHelper.GetParent(curr);
        }
        return false;
    }

    static bool IsInsideEditableTextInput(DependencyObject el)
    {
        var curr = VisualTreeHelper.GetParent(el);
        while (curr != null)
        {
            if (curr is TextBox or RichTextBox or PasswordBox)
                return true;
            curr = VisualTreeHelper.GetParent(curr);
        }
        return false;
    }

    static bool IsUserDataBoundTextBlock(TextBlock tb)
    {
        if (tb.Name is "TitleText" or "MetaText")
            return true;
        var b = System.Windows.Data.BindingOperations.GetBinding(tb, TextBlock.TextProperty);
        if (b != null && b.RelativeSource == null && string.IsNullOrEmpty(b.ElementName) && b.Source == null)
        {
            string? propPath = b.Path?.Path;
            if (propPath is "Title" or "Description" or "Name" or "Url" or "FileName" or "FoundOn" or "Pattern" or "User" or "LinkText" or "Path")
                return true;
        }
        DependencyObject? curr = VisualTreeHelper.GetParent(tb);
        while (curr != null)
        {
            if (curr is TreeView tv && tv.Name is "FolderTree" or "FoundTree")
                return true;
            curr = VisualTreeHelper.GetParent(curr);
        }
        return false;
    }

    static bool IsUntranslatedKey(string text)
    {
        if (text.Contains(' ') || text.Contains('/') || text.Contains('\\') || text.Contains(':'))
            return false;
        if (!KeyPattern.IsMatch(text))
            return false;
        // Exclude file names or domain names
        if (UrlOrPathRegex.IsMatch(text))
            return false;
        if (text.EndsWith(".com", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".org", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".net", StringComparison.OrdinalIgnoreCase)
            || text.EndsWith(".io", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    static List<string> FindUnallowedEnglishWordsInArabic(string text)
    {
        string cleaned = UrlOrPathRegex.Replace(text, " ");
        var bad = new List<string>();
        foreach (Match m in LatinWordRegex.Matches(cleaned))
        {
            string word = m.Value;
            if (!AllowedTechnicalWordsInArabic.Contains(word))
            {
                if (!bad.Contains(word, StringComparer.OrdinalIgnoreCase))
                    bad.Add(word);
            }
        }
        return bad;
    }

    static bool TryGetEffectiveColor(Brush? brush, out Color color)
    {
        if (brush is SolidColorBrush scb && scb.Color.A > 10)
        {
            color = scb.Color;
            return true;
        }
        color = default;
        return false;
    }

    static bool TryFindAncestorBackgroundColor(DependencyObject start, out Color color)
    {
        var curr = start;
        while (curr != null)
        {
            Brush? bg = curr switch
            {
                TextBlock tb => tb.Background,
                Border b => b.Background,
                Panel p => p.Background,
                Control c => c.Background,
                _ => null,
            };
            if (bg is SolidColorBrush scb && scb.Color.A >= 128)
            {
                color = scb.Color;
                return true;
            }
            curr = VisualTreeHelper.GetParent(curr);
        }
        color = Colors.White;
        return false;
    }

    static Color CompositeOver(Color fg, Color bg)
    {
        double a = fg.A / 255.0;
        byte r = (byte)Math.Clamp((int)Math.Round(fg.R * a + bg.R * (1 - a)), 0, 255);
        byte g = (byte)Math.Clamp((int)Math.Round(fg.G * a + bg.G * (1 - a)), 0, 255);
        byte b = (byte)Math.Clamp((int)Math.Round(fg.B * a + bg.B * (1 - a)), 0, 255);
        return Color.FromRgb(r, g, b);
    }

    public static double ContrastRatio(Color c1, Color c2)
    {
        double l1 = RelativeLuminance(c1);
        double l2 = RelativeLuminance(c2);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    static double RelativeLuminance(Color c)
    {
        static double Chan(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Chan(c.R) + 0.7152 * Chan(c.G) + 0.0722 * Chan(c.B);
    }

    static Rect GetBoundsInRoot(FrameworkElement el, FrameworkElement root)
    {
        try
        {
            if (ReferenceEquals(el, root))
                return new Rect(0, 0, root.ActualWidth, root.ActualHeight);
            var transform = el.TransformToVisual(root);
            return transform.TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    static string GetPath(DependencyObject el, DependencyObject root)
    {
        var parts = new Stack<string>();
        var curr = el;
        while (curr != null)
        {
            string name = curr.GetType().Name;
            if (curr is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
                name += $"#{fe.Name}";
            parts.Push(name);
            if (ReferenceEquals(curr, root)) break;
            curr = VisualTreeHelper.GetParent(curr);
        }
        return string.Join(" > ", parts);
    }

    static string DescribeElement(FrameworkElement el)
    {
        string s = el.GetType().Name;
        if (!string.IsNullOrEmpty(el.Name)) s += $"#{el.Name}";
        if (el is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
            s += $"(\"{Truncate(tb.Text.Trim(), 25)}\")";
        else if (el is ContentControl cc && cc.Content is string cs && !string.IsNullOrWhiteSpace(cs))
            s += $"(\"{Truncate(cs.Trim(), 25)}\")";
        return s;
    }

    static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
