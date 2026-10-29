using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;
using MonoTorrent;

namespace Golan.Gdm.UI;

public partial class TorrentFilesWindow : Window
{
    public sealed record PriorityOption(TorrentFilePriority Value, string Label);

    private readonly ObservableCollection<TorrentFileEntry> _entries = [];
    private readonly DownloadItem? _item;

    public IReadOnlyList<TorrentFileEntry> Entries => _entries;
    public bool SequentialDownload => SequentialCheck.IsChecked == true;

    public TorrentFilesWindow(string torrentName, IEnumerable<TorrentFileEntry> entries, bool sequential = false, DownloadItem? item = null)
    {
        InitializeComponent();
        FlowDirection = Loc.FlowDirection;
        _item = item;
        TorrentNameText.Text = torrentName;
        SequentialCheck.IsChecked = sequential;

        PriorityColumn.ItemsSource = new List<PriorityOption>
        {
            new(TorrentFilePriority.DoNotDownload, TorrentText.T("torrent.priority.skip")),
            new(TorrentFilePriority.Low, TorrentText.T("torrent.priority.low")),
            new(TorrentFilePriority.Normal, TorrentText.T("torrent.priority.normal")),
            new(TorrentFilePriority.High, TorrentText.T("torrent.priority.high")),
        };

        foreach (var entry in entries)
        {
            entry.PropertyChanged += Entry_PropertyChanged;
            _entries.Add(entry);
        }

        Closed += (_, _) =>
        {
            foreach (var entry in _entries)
                entry.PropertyChanged -= Entry_PropertyChanged;
        };

        FilesGrid.ItemsSource = _entries;
        PopulateFolderTree();
        UpdateSummary();
    }

    public static TorrentFilesWindow FromTorrent(MonoTorrent.Torrent torrent, DownloadItem? item = null)
    {
        var list = new List<TorrentFileEntry>(torrent.Files.Count);
        HashSet<int>? selectedSet = item?.TorrentSelectedFiles != null
            ? new HashSet<int>(item.TorrentSelectedFiles)
            : null;

        for (int i = 0; i < torrent.Files.Count; i++)
        {
            var f = torrent.Files[i];
            var prio = TorrentFilePriority.Normal;
            if (item?.TorrentFilePriorities != null && item.TorrentFilePriorities.TryGetValue(i, out var savedPrio))
            {
                prio = savedPrio;
            }
            else if (selectedSet != null)
            {
                prio = selectedSet.Contains(i) ? TorrentFilePriority.Normal : TorrentFilePriority.DoNotDownload;
            }
            list.Add(new TorrentFileEntry(i, f.Path, f.Length, prio));
        }

        return new TorrentFilesWindow(torrent.Name ?? "Torrent", list, item?.TorrentSequential ?? false, item);
    }

    private void PopulateFolderTree()
    {
        FolderTree.Items.Clear();
        var rootItem = new TreeViewItem
        {
            Header = $"{TorrentText.T("torrent.files.allFolders")} ({_entries.Count})",
            Tag = "",
            IsExpanded = true,
            IsSelected = true
        };

        var folderNodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            string normalized = entry.Path.Replace('\\', '/');
            string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) continue;

            string currentPrefix = "";
            TreeViewItem parent = rootItem;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                currentPrefix = currentPrefix.Length == 0 ? parts[i] : $"{currentPrefix}/{parts[i]}";
                if (!folderNodes.TryGetValue(currentPrefix, out var node))
                {
                    node = new TreeViewItem
                    {
                        Header = parts[i],
                        Tag = currentPrefix + "/",
                        IsExpanded = i == 0
                    };
                    folderNodes[currentPrefix] = node;
                    parent.Items.Add(node);
                }
                parent = node;
            }
        }

        FolderTree.Items.Add(rootItem);
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (FolderTree.SelectedItem is not TreeViewItem item || item.Tag is not string prefix || string.IsNullOrEmpty(prefix))
        {
            FilesGrid.ItemsSource = _entries;
            return;
        }

        FilesGrid.ItemsSource = _entries
            .Where(x => x.Path.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TorrentFileEntry.Selected) or nameof(TorrentFileEntry.Priority))
        {
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        int selectedCount = _entries.Count(e => e.Selected && e.Priority != TorrentFilePriority.DoNotDownload);
        long selectedBytes = TorrentSelection.ComputeSelectedSize(_entries);
        SummaryText.Text = TorrentText.T("torrent.files.selectedSummary", selectedCount, _entries.Count, Fmt.Size(selectedBytes));
        ExeWarningBanner.Visibility = TorrentSelection.HasSelectedExecutable(_entries)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (OkBtn != null)
            OkBtn.IsEnabled = selectedCount > 0;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        TorrentSelection.SelectAll(_entries);
        UpdateSummary();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        TorrentSelection.SelectNone(_entries);
        UpdateSummary();
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_item != null)
        {
            _item.TorrentSequential = SequentialDownload;
            _item.TorrentSelectedFiles = TorrentSelection.GetSelectedIndices(_entries);
            _item.TorrentFilePriorities = _entries.ToDictionary(x => x.Index, x => x.Priority);
            long selectedBytes = TorrentSelection.ComputeSelectedSize(_entries);
            if (selectedBytes > 0)
                _item.TotalSize = selectedBytes;

            if (TorrentService.TryGetActiveManager(_item.Id, out var activeMgr) && activeMgr != null)
            {
                try
                {
                    await TorrentSelection.ApplyToManagerAsync(activeMgr, _item);
                }
                catch (Exception ex)
                {
                    Log.Write("torrent file selection apply failed: " + ex.Message);
                }
            }
            App.Instance.Manager.Save();
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
