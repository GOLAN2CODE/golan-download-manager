using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class HashWindow : Window
{
    readonly string _filePath;
    readonly CancellationTokenSource _cts = new();
    Dictionary<HashAlgorithmName, string> _results = [];
    bool _started;

    public HashWindow(string filePath, string? expectedHash = null)
    {
        InitializeComponent();
        _filePath = filePath;
        var fileName = Path.GetFileName(filePath);
        FileNameText.Text = fileName;
        FileNameText.FlowDirection = TextDir.IsRtl(fileName) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        if (!string.IsNullOrWhiteSpace(expectedHash))
            CompareBox.Text = expectedHash.Trim();

        Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await ComputeAllAsync();
        };
        Closed += (_, _) => { try { _cts.Cancel(); _cts.Dispose(); } catch { /* already disposed */ } };
    }

    public HashWindow(DownloadItem item)
        : this(item.FullPath, item.ExpectedHash)
    {
    }

    async Task ComputeAllAsync()
    {
        try
        {
            StatusLabel.Text = Loc.T($"hash.win.computing", 0);
            var progress = new Progress<double>(p =>
            {
                int pct = (int)Math.Round(p * 100);
                HashProgress.Value = pct;
                StatusLabel.Text = Loc.T($"hash.win.computing", pct);
            });

            _results = await Integrity.ComputeAsync(_filePath, Integrity.AllAlgorithms, progress, _cts.Token);
            Md5Box.Text = _results.GetValueOrDefault(HashAlgorithmName.MD5, "");
            Sha1Box.Text = _results.GetValueOrDefault(HashAlgorithmName.SHA1, "");
            Sha256Box.Text = _results.GetValueOrDefault(HashAlgorithmName.SHA256, "");
            Sha512Box.Text = _results.GetValueOrDefault(HashAlgorithmName.SHA512, "");
            HashProgress.Value = 100;
            StatusLabel.Text = Loc.T($"hash.win.done");
            UpdateComparison();
        }
        catch (OperationCanceledException)
        {
            // Hash computation cancelled when window closed.
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = TryFindResource("Danger") as Brush ?? Brushes.Transparent;
            StatusLabel.Text = Loc.T("hash.status.error", FriendlyError.Format(ex));
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } btn) return;
        var text = tag switch
        {
            "MD5" => Md5Box.Text,
            "SHA1" => Sha1Box.Text,
            "SHA256" => Sha256Box.Text,
            "SHA512" => Sha512Box.Text,
            _ => "",
        };
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            App.Instance?.MainView?.SuppressClipboard();
            Clipboard.SetText(text);
            foreach (var b in new[] { CopyMd5Btn, CopySha1Btn, CopySha256Btn, CopySha512Btn })
            {
                if (b != null) b.Content = Loc.T("hash.win.copy");
            }
            btn.Content = Loc.T("hash.win.copied");
        }
        catch (Exception ex)
        {
            Log.Write("hash clipboard copy failed: " + ex.Message);
        }
    }

    public static string NormalizeExpectedHashInput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        if (HashSpec.TryParse(s, out _)) return s;

        // Support "SHA256 = <hex>" or "<hex>  filename.iso" (sha256sum / CertUtil formats)
        int eqIdx = s.IndexOf('=');
        if (eqIdx > 0)
        {
            var lhs = s[..eqIdx].Trim();
            var rhs = s[(eqIdx + 1)..].Trim();
            int parenIdx = lhs.IndexOf('(');
            if (parenIdx > 0) lhs = lhs[..parenIdx].Trim();
            var candidate = $"{lhs}:{rhs}";
            if (HashSpec.TryParse(candidate, out _)) return candidate;
            if (HashSpec.TryParse(rhs, out _)) return rhs;
        }

        var firstToken = s.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (HashSpec.TryParse(firstToken, out _)) return firstToken;
        return s;
    }

    void CompareBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateComparison();

    void UpdateComparison()
    {
        var input = NormalizeExpectedHashInput(CompareBox.Text);
        if (string.IsNullOrEmpty(input) || _results.Count == 0)
        {
            CompareResultText.Visibility = Visibility.Collapsed;
            return;
        }

        CompareResultText.Visibility = Visibility.Visible;
        if (!HashSpec.TryParse(input, out var spec) || spec == null)
        {
            CompareResultText.Text = Loc.T("hash.win.invalid_hash");
            CompareResultText.Foreground = TryFindResource("Warning") as Brush ?? Brushes.Transparent;
            return;
        }

        if (!_results.TryGetValue(spec.Algorithm, out var actualHex))
        {
            CompareResultText.Visibility = Visibility.Collapsed;
            return;
        }

        bool match = Integrity.VerifyHex(actualHex, input);
        var algoLabel = spec.Algorithm.Name ?? "SHA256";
        CompareResultText.Text = match
            ? Loc.T("hash.win.match", algoLabel)
            : Loc.T("hash.win.no_match", algoLabel);
        CompareResultText.Foreground = TryFindResource(match ? "Success" : "Danger") as Brush
            ?? Brushes.Transparent;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
