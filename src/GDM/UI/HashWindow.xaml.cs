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

    public HashWindow(string filePath, string? expectedHash = null)
    {
        InitializeComponent();
        _filePath = filePath;
        FileNameText.Text = Path.GetFileName(filePath);
        if (!string.IsNullOrWhiteSpace(expectedHash))
            CompareBox.Text = expectedHash.Trim();

        Loaded += async (_, _) => await ComputeAllAsync();
        Closed += (_, _) => { _cts.Cancel(); _cts.Dispose(); };
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
            StatusLabel.Text = Loc.T($"hash.status.error", ex.Message);
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
            Clipboard.SetText(text);
            btn.Content = Loc.T($"hash.win.copied");
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Write("hash clipboard copy failed: " + ex.Message);
        }
    }

    void CompareBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateComparison();

    void UpdateComparison()
    {
        var input = CompareBox.Text.Trim();
        if (string.IsNullOrEmpty(input) || _results.Count == 0)
        {
            CompareResultText.Visibility = Visibility.Collapsed;
            return;
        }

        CompareResultText.Visibility = Visibility.Visible;
        if (!HashSpec.TryParse(input, out var spec) || spec == null)
        {
            CompareResultText.Text = Loc.T($"hash.win.invalid_hash");
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
            ? Loc.T($"hash.win.match", algoLabel)
            : Loc.T($"hash.win.no_match", algoLabel);
        CompareResultText.Foreground = TryFindResource(match ? "Success" : "Danger") as Brush
            ?? Brushes.Transparent;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
