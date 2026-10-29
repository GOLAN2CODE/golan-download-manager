using System.Linq;
using System.Windows;
using System.Windows.Media;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class BatchPatternWindow : Window
{
    readonly DownloadManager _manager;

    public BatchPatternWindow(DownloadManager manager)
    {
        InitializeComponent();
        _manager = manager;
        PatternBox.TextChanged += Changed;
        FromBox.TextChanged += Changed;
        ToBox.TextChanged += Changed;
        WidthBox.TextChanged += Changed;
        NumbersRadio.Checked += Changed;
        LettersRadio.Checked += Changed;
        Preview();
    }

    void Changed(object? s, RoutedEventArgs e) => Preview();

    void Preview()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var width = int.TryParse(WidthBox.Text, out var w) ? w : 0;
        var v = Transfer.ExpandBatch(PatternBox.Text, LettersRadio.IsChecked == true, FromBox.Text, ToBox.Text, width)
            .Where(FileNames.IsHttpUrl)
            .ToList();
        if (v.Count == 0)
        {
            PreviewText.Text = Loc.T("win.batchpattern.preview_empty");
            return;
        }
        var lines = v.Take(3).Select((x, i) => i == 0
            ? Loc.T("win.batchpattern.preview_first", x)
            : i == 1
                ? Loc.T("win.batchpattern.preview_second", x)
                : "…");
        PreviewText.Text = string.Join("\n", lines) + (v.Count > 3 ? "\n" + Loc.T("win.batchpattern.preview_last", v[^1]) : "");
    }

    void ShowError(string text)
    {
        ErrorText.Foreground = (Brush)FindResource("Danger");
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    void Ok_Click(object s, RoutedEventArgs e)
    {
        var width = int.TryParse(WidthBox.Text, out var w) ? w : 0;
        var urls = Transfer.ExpandBatch(PatternBox.Text, LettersRadio.IsChecked == true, FromBox.Text, ToBox.Text, width)
            .Where(FileNames.IsHttpUrl)
            .ToList();
        if (urls.Count == 0)
        {
            ShowError(Loc.T("win.batchpattern.err_invalid"));
            return;
        }
        var pick = App.Instance.PickQueue(this, urls.Count);
        if (pick == null)
        {
            return;
        }
        var added = 0;
        foreach (var url in urls)
        {
            if (_manager.FindDuplicate(url) != null)
            {
                continue;
            }
            var item = _manager.CreateHttp(url);
            item.Login = LoginBox.Text;
            item.Password = PasswordBox.Password;
            _manager.Add(item, false, pick.Value.Queue);
            added++;
        }
        if (pick.Value.Start && added > 0)
        {
            _manager.StartQueue(pick.Value.Queue);
        }
        if (added > 0)
        {
            DialogResult = true;
        }
    }
}
