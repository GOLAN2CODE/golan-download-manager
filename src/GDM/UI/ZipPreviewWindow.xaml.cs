using System.Linq;
using System.Threading;
using System.Windows;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class ZipPreviewWindow : Window
{
    readonly DownloadItem _item;
    readonly CancellationTokenSource _cts = new();
    bool _loaded;

    public ZipPreviewWindow(DownloadItem item)
    {
        InitializeComponent();
        _item = item;
        Loaded += LoadedAsync;
        Closed += (_, _) => { try { _cts.Cancel(); _cts.Dispose(); } catch { /* already disposed */ } };
    }

    async void LoadedAsync(object? s, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            StateText.Text = Loc.T("win.zip.loading");
            LoadProgress.Visibility = Visibility.Visible;
            var entries = await ZipPreview.ListAsync(_item, _cts.Token);
            EntriesGrid.ItemsSource = entries.Select(x => new ZipEntryRow(
                x.Name,
                Fmt.Size(x.Size),
                Fmt.Size(x.CompressedSize),
                x.Modified == DateTime.MinValue ? "—" : x.Modified.ToString("yyyy/MM/dd HH:mm"),
                x.Size,
                x.CompressedSize,
                x.Modified
            )).ToList();
            var totalSize = Fmt.Size(entries.Sum(x => x.Size));
            var compressedSize = Fmt.Size(entries.Sum(x => x.CompressedSize));
            StateText.Text = Loc.T("win.zip.summary", entries.Count, totalSize, compressedSize);
        }
        catch (OperationCanceledException)
        {
            // Preview cancelled when window closed.
        }
        catch (Exception ex)
        {
            EntriesGrid.Visibility = Visibility.Collapsed;
            if (TryFindResource("Danger") is System.Windows.Media.Brush danger)
                StateText.Foreground = danger;
            StateText.Text = Loc.T("win.zip.error", FriendlyError.Format(ex));
        }
        finally
        {
            LoadProgress.IsIndeterminate = false;
            LoadProgress.Visibility = Visibility.Collapsed;
        }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    public sealed record ZipEntryRow(
        string Name,
        string Size,
        string CompressedSize,
        string Modified,
        long RawSize = 0,
        long RawCompressedSize = 0,
        DateTime RawModified = default);
}
