using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Golan.Gdm.Tests.Render;

public static class LayoutInspectorTests
{
    public static (int Passed, int Failed) RunAll()
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

        // 1. Detects clipped / trimmed TextBlock
        {
            var root = new Grid { Width = 200, Height = 80, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            var tb = new TextBlock
            {
                Name = "ClippedLabel",
                Width = 40,
                Text = "This is a very long piece of text that cannot fit in 40px",
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = Brushes.Black,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            root.Children.Add(tb);
            LayoutHelper(root, 200, 80);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("Detects clipped/trimmed TextBlock", issues.Any(i => i.Kind == IssueKind.TextClipped));
        }

        // 2. Detects control overflowing window bounds
        {
            var root = new Canvas { Width = 200, Height = 100, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            var btn = new Button
            {
                Name = "OverflowBtn",
                Content = "OK",
                Width = 80,
                Height = 30,
                Foreground = Brushes.Black,
                Background = Brushes.LightGray,
            };
            Canvas.SetLeft(btn, 160); // 160 + 80 = 240 > 200
            Canvas.SetTop(btn, 20);
            root.Children.Add(btn);
            LayoutHelper(root, 200, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("Detects control overflowing window bounds", issues.Any(i => i.Kind == IssueKind.OverflowBounds));
        }

        // 3. Detects overlapping siblings
        {
            var root = new Canvas { Width = 300, Height = 100, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            var b1 = new Button { Name = "Btn1", Content = "First", Width = 100, Height = 32, Foreground = Brushes.Black, Background = Brushes.LightGray };
            var b2 = new Button { Name = "Btn2", Content = "Second", Width = 100, Height = 32, Foreground = Brushes.Black, Background = Brushes.LightGray };
            Canvas.SetLeft(b1, 20); Canvas.SetTop(b1, 20);
            Canvas.SetLeft(b2, 60); Canvas.SetTop(b2, 25); // overlaps b1
            root.Children.Add(b1);
            root.Children.Add(b2);
            LayoutHelper(root, 300, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("Detects overlapping siblings", issues.Any(i => i.Kind == IssueKind.OverlappingSiblings));
        }

        // 4. Detects untranslated localization key
        {
            var root = new StackPanel { Width = 300, Height = 100, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            root.Children.Add(new TextBlock { Name = "KeyLabel", Text = "win.options.missing_key", Foreground = Brushes.Black });
            LayoutHelper(root, 300, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("Detects untranslated key pattern", issues.Any(i => i.Kind == IssueKind.UntranslatedKey));
        }

        // 5a. Detects Arabic text in English mode
        {
            var root = new StackPanel { Width = 300, Height = 100, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            root.Children.Add(new TextBlock { Name = "ArInEn", Text = "إعدادات التحميل", Foreground = Brushes.Black });
            LayoutHelper(root, 300, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("Detects Arabic text in English mode", issues.Any(i => i.Kind == IssueKind.LanguageMismatch));
        }

        // 5b. Detects English prose text in Arabic mode
        {
            var root = new StackPanel { Width = 300, Height = 100, Background = Brushes.White, FlowDirection = FlowDirection.RightToLeft };
            root.Children.Add(new TextBlock { Name = "EnInAr", Text = "Download settings and options", Foreground = Brushes.Black });
            LayoutHelper(root, 300, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "ar", "light");
            AssertTrue("Detects English text in Arabic mode", issues.Any(i => i.Kind == IssueKind.LanguageMismatch));
        }

        // 6. Detects wrong FlowDirection (LTR root in Arabic mode)
        {
            var root = new StackPanel { Width = 300, Height = 100, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            root.Children.Add(new TextBlock { Text = "حسناً", Foreground = Brushes.Black });
            LayoutHelper(root, 300, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "ar", "light");
            AssertTrue("Detects wrong FlowDirection in Arabic mode", issues.Any(i => i.Kind == IssueKind.WrongFlowDirection));
        }

        // 7. Detects invisible / low-contrast text in dark mode
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1D, 0x24));
            var fg = new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2E));
            var root = new Border { Width = 300, Height = 100, Background = bg, FlowDirection = FlowDirection.LeftToRight };
            root.Child = new TextBlock { Name = "DarkOnDark", Text = "Hidden dark text", Foreground = fg };
            LayoutHelper(root, 300, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "dark");
            AssertTrue("Detects invisible/low-contrast text", issues.Any(i => i.Kind == IssueKind.LowContrast));
        }

        // 8. Clean well-formed layout produces zero false positives
        {
            var root = new StackPanel { Width = 300, Height = 100, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            root.Children.Add(new TextBlock { Name = "CleanTitle", Text = "Download complete", Foreground = Brushes.Black, Margin = new Thickness(8) });
            LayoutHelper(root, 300, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("Clean layout produces 0 issues", issues.Count == 0, $"got {issues.Count}");
        }

        // 9. ControlTemplate internal panels (TemplatedParent != null) do not trigger false-positive OverlappingSiblings
        {
            var tmpl = new ControlTemplate(typeof(TextBox));
            var gridFactory = new FrameworkElementFactory(typeof(Grid));
            var scrollFactory = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            var hintFactory = new FrameworkElementFactory(typeof(TextBlock));
            hintFactory.SetValue(TextBlock.TextProperty, "Search placeholder");
            hintFactory.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
            gridFactory.AppendChild(scrollFactory);
            gridFactory.AppendChild(hintFactory);
            tmpl.VisualTree = gridFactory;

            var root = new StackPanel { Width = 300, Height = 100, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            var tb = new TextBox { Width = 220, Height = 32, Template = tmpl, Background = Brushes.White, Foreground = Brushes.Black };
            root.Children.Add(tb);
            LayoutHelper(root, 300, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("ControlTemplate internal watermark grid does not report OverlappingSiblings",
                !issues.Any(i => i.Kind == IssueKind.OverlappingSiblings));
        }

        // 10. Detects broken ComboBox DisplayMemberPath binding to ValueTuple fields
        {
            var root = new StackPanel { Width = 300, Height = 100, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            var cb = new ComboBox
            {
                Width = 200,
                Height = 32,
                ItemsSource = new (string Key, string Label)[] { ("ar", "Arabic"), ("en", "English") },
                DisplayMemberPath = "Label",
                SelectedValuePath = "Key",
                SelectedValue = "en",
            };
            root.Children.Add(cb);
            LayoutHelper(root, 300, 100);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("Detects ComboBox DisplayMemberPath bound to ValueTuple without property",
                issues.Any(i => i.ElementType == "ComboBox" && i.Kind == IssueKind.TextClipped));
        }

        // 11. Detects TextBlock with TextTrimming=None clipped by constrained DockPanel layout slot
        {
            var root = new DockPanel { Width = 90, Height = 40, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            var tb = new TextBlock
            {
                Name = "SlotClippedLabel",
                Text = "Synchronization Queue Very Long Name",
                TextTrimming = TextTrimming.None,
                TextWrapping = TextWrapping.NoWrap,
                Foreground = Brushes.Black,
            };
            root.Children.Add(tb);
            LayoutHelper(root, 90, 40);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("Detects TextBlock with TextTrimming=None clipped by layout slot",
                issues.Any(i => i.Kind == IssueKind.TextClipped && i.ElementName == "SlotClippedLabel"));
        }

        // 12. TextBlock with its own dark Background and white Foreground does not trigger false-positive LowContrast
        {
            var root = new StackPanel { Width = 200, Height = 60, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            var badge = new TextBlock
            {
                Name = "Dot1",
                Text = "1",
                Width = 24,
                Height = 24,
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
                Foreground = Brushes.White,
            };
            root.Children.Add(badge);
            LayoutHelper(root, 200, 60);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("TextBlock with own dark Background and white Foreground has valid contrast",
                !issues.Any(i => i.Kind == IssueKind.LowContrast && i.ElementName == "Dot1"),
                string.Join("; ", issues.Select(i => i.Detail)));
        }

        // 13. Detects child TextBlock with explicit LTR FlowDirection containing Arabic text inside RTL root
        {
            var root = new StackPanel { Width = 300, Height = 60, Background = Brushes.White, FlowDirection = FlowDirection.RightToLeft };
            var child = new TextBlock
            {
                Name = "SaveToText",
                Text = "حفظ في: C:\\Downloads",
                FlowDirection = FlowDirection.LeftToRight,
                Foreground = Brushes.Black,
            };
            root.Children.Add(child);
            LayoutHelper(root, 300, 60);

            var issues = LayoutInspector.Inspect(root, "TestWin", "ar", "light");
            AssertTrue("Detects child TextBlock with explicit LTR FlowDirection on Arabic text",
                issues.Any(i => i.Kind == IssueKind.WrongFlowDirection && i.ElementName == "SaveToText"));
        }

        // 14. Detects wrapped TextBlock overflowing vertical height
        {
            var root = new Grid { Width = 120, Height = 60, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            var wrapped = new TextBlock
            {
                Name = "WrappedClipped",
                Width = 90,
                Height = 18,
                TextWrapping = TextWrapping.Wrap,
                Text = "This wrapped sentence spans multiple lines and overflows an 18px tall box.",
                Foreground = Brushes.Black,
            };
            root.Children.Add(wrapped);
            LayoutHelper(root, 120, 60);

            var issues = LayoutInspector.Inspect(root, "TestWin", "en", "light");
            AssertTrue("Detects wrapped TextBlock vertical height clipping",
                issues.Any(i => i.Kind == IssueKind.TextClipped && i.ElementName == "WrappedClipped"));
        }

        // 15. Exempts elements inside ScrollViewer from OverflowBounds and URLs/filenames from LanguageMismatch
        {
            var root = new Grid { Width = 240, Height = 80, Background = Brushes.White, FlowDirection = FlowDirection.RightToLeft };
            var sv = new ScrollViewer { Width = 240, Height = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Name = "UrlLabel",
                Text = "https://releases.ubuntu.com/24.04/ubuntu-24.04.1-desktop-amd64.iso",
                Foreground = Brushes.Black,
                Margin = new Thickness(0, 0, 0, 100),
            });
            sp.Children.Add(new Button
            {
                Name = "BelowFoldButton",
                Content = "حسناً",
                Width = 80,
                Height = 32,
                Foreground = Brushes.Black,
                Background = Brushes.LightGray,
            });
            sv.Content = sp;
            root.Children.Add(sv);
            LayoutHelper(root, 240, 80);

            var issues = LayoutInspector.Inspect(root, "TestWin", "ar", "light");
            AssertTrue("Exempts ScrollViewer content from OverflowBounds and URLs from LanguageMismatch",
                !issues.Any(i => i.Kind is IssueKind.OverflowBounds or IssueKind.LanguageMismatch),
                string.Join("; ", issues.Select(i => $"{i.Kind}:{i.ElementName}")));
        }

        // 16. Data-bound user/remote content (e.g. {Binding Title} or {Binding Description}) does not trigger false-positive LanguageMismatch in Arabic mode
        {
            var root = new StackPanel { Width = 300, Height = 80, Background = Brushes.White, FlowDirection = FlowDirection.RightToLeft };
            var boundTb = new TextBlock
            {
                Name = "BoundItemTitle",
                Foreground = Brushes.Black,
                DataContext = new Golan.Gdm.Core.PlaylistEntry { Title = "Release Notes", Url = "https://example.com/ep1" },
            };
            var expr = System.Windows.Data.BindingOperations.SetBinding(
                boundTb,
                TextBlock.TextProperty,
                new System.Windows.Data.Binding("Title"));
            expr.UpdateTarget();
            root.Children.Add(boundTb);
            LayoutHelper(root, 300, 80);

            var issues = LayoutInspector.Inspect(root, "TestWin", "ar", "light");
            AssertTrue("Data-bound item Title does not trigger false-positive LanguageMismatch in Arabic mode",
                boundTb.Text == "Release Notes" && !issues.Any(i => i.Kind == IssueKind.LanguageMismatch),
                string.Join("; ", issues.Select(i => i.Detail)));
        }

        return (passed, failed);
    }

    static void LayoutHelper(FrameworkElement el, double w, double h)
    {
        el.Measure(new Size(w, h));
        el.Arrange(new Rect(0, 0, w, h));
        el.UpdateLayout();
    }
}
