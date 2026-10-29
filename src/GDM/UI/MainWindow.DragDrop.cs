using System.IO;
using System.Windows;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;

namespace Golan.Gdm.UI;

public partial class MainWindow
{
    void Window_DragOver(object sender, DragEventArgs e)
    {
        bool hasTorrentFile = DroppedTorrentFiles(e.Data).Count > 0;
        bool hasUrl = e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent("UniformResourceLocatorW");
        e.Effects = hasTorrentFile ? DragDropEffects.Copy : (hasUrl ? DragDropEffects.Link : DragDropEffects.None);
        e.Handled = true;
    }

    async void Window_Drop(object sender, DragEventArgs e)
    {
        var torrentFiles = DroppedTorrentFiles(e.Data);
        if (torrentFiles.Count > 0)
        {
            foreach (var file in torrentFiles)
                await App.Instance.OpenTorrentFileAsync(file, this);
            e.Handled = true;
            return;
        }

        var text = DroppedUrl(e.Data);
        if (!string.IsNullOrWhiteSpace(text) && (FileNames.IsHttpUrl(text) || MagnetValidator.IsMagnetUri(text)))
            App.Instance.ShowAddUrl(text);
        e.Handled = true;
    }

    public static IReadOnlyList<string> DroppedTorrentFiles(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files)
            return [];
        return files
            .Where(f => !string.IsNullOrWhiteSpace(f) && f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            .ToArray();
    }

    public static string? DroppedUrl(IDataObject data)
    {
        string? text = null;
        if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream ms)
            text = System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0');
        text ??= data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
        return text?.Trim();
    }
}
