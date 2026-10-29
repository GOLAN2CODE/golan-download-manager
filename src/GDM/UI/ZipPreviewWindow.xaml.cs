using System.Linq;
using System.Threading;
using System.Windows;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class ZipPreviewWindow : Window
{
    readonly DownloadItem _item;
    readonly CancellationTokenSource _cts = new();

    public ZipPreviewWindow(DownloadItem item)
    {
        InitializeComponent();
        _item = item;
        Loaded += LoadedAsync;
        Closed += (_, _) => _cts.Cancel();
    }

    async void LoadedAsync(object? s, RoutedEventArgs e)
    {
        try
        {
            StateText.Text = Loc.T("win.zip.loading");
            var entries = await ZipPreview.ListAsync(_item, _cts.Token);
            EntriesGrid.ItemsSource = entries.Select(x => new ZipEntryRow(
                x.Name,
                Fmt.Size(x.Size),
                Fmt.Size(x.CompressedSize),
                x.Modified == DateTime.MinValue ? "—" : x.Modified.ToString("yyyy/MM/dd HH:mm")
            )).ToList();
            var totalSize = Fmt.Size(entries.Sum(x => x.Size));
            var compressedSize = Fmt.Size(entries.Sum(x => x.CompressedSize));
            StateText.Text = Loc.T("win.zip.summary", entries.Count, totalSize, compressedSize);
        }
        catch (Exception ex)
        {
            EntriesGrid.Visibility = Visibility.Collapsed;
            StateText.Text = Loc.T("win.zip.error", ex.Message);
        }
    }

    public sealed record ZipEntryRow(string Name, string Size, string CompressedSize, string Modified);
}
