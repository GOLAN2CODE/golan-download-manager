using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Golan.Gdm.Core;
using Microsoft.Win32;

namespace Golan.Gdm.UI;

public partial class IdmImportWindow : Window
{
    readonly DownloadManager _manager;
    readonly ObservableCollection<CandidateRow> _rows = [];
    int _malformedSkipped;

    public sealed record QueueOption(Guid? Id, string Label);

    public sealed class CandidateRow : ObservableObject
    {
        public required IdmCandidate Candidate { get; init; }
        public required string FileName { get; init; }
        public required string SizeText { get; init; }
        public required string StatusText { get; init; }
        public required string Url { get; init; }
        public required bool IsDuplicate { get; init; }
        public bool Selectable => !IsDuplicate;

        bool _selected;
        public bool Selected
        {
            get => _selected;
            set => Set(ref _selected, value);
        }
    }

    public IdmImportWindow(DownloadManager manager)
    {
        InitializeComponent();
        FlowDirection = Loc.FlowDirection;
        _manager = manager;

        CandidatesGrid.ItemsSource = _rows;

        var queueOptions = new List<QueueOption>
        {
            new(null, Loc.T("idm.queue.none")),
        };
        foreach (var q in _manager.Settings.Queues)
            queueOptions.Add(new(q.Id, q.Name));
        QueueCombo.ItemsSource = queueOptions;
        QueueCombo.SelectedIndex = 0;

        UpdateSummary();
    }

    void BrowseEf2_Click(object sender, RoutedEventArgs e)
    {
        Ef2Radio.IsChecked = true;
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("idm.title"),
            Filter = Loc.T("idm.ef2_filter"),
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var res = IdmImport.ParseEf2File(dlg.FileName);
            LoadParseResult(res);
        }
        catch (Exception ex)
        {
            App.Instance?.Notify(Loc.T("idm.title"), ex.Message, error: true);
        }
    }

    void ScanRegistry_Click(object sender, RoutedEventArgs e)
    {
        RegistryRadio.IsChecked = true;
        try
        {
            var res = IdmImport.ReadRegistry(Win32CurrentUserRegistryView.Instance);
            LoadParseResult(res);
        }
        catch (Exception ex)
        {
            App.Instance?.Notify(Loc.T("idm.title"), ex.Message, error: true);
        }
    }

    void LoadParseResult(IdmParseResult result)
    {
        _rows.Clear();
        _malformedSkipped = result.SkippedCount;

        var existingNorm = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _manager.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.Url))
                existingNorm.Add(IdmImport.NormalizeUrlForDedupe(item.Url));
            if (!string.IsNullOrWhiteSpace(item.ResolvedUrl))
                existingNorm.Add(IdmImport.NormalizeUrlForDedupe(item.ResolvedUrl));
        }

        var batchSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in result.Candidates)
        {
            bool isDup = existingNorm.Contains(c.NormalizedUrl) || !batchSeen.Add(c.NormalizedUrl);
            bool completedExists = IdmImport.TryResolveCompletedLocalFile(c, out _, out var cleanName, out _) && !isDup;

            string statusText = isDup
                ? Loc.T("idm.status.duplicate")
                : completedExists
                    ? Loc.T("idm.status.completed")
                    : Loc.T("idm.status.paused");

            _rows.Add(new CandidateRow
            {
                Candidate = c,
                FileName = cleanName,
                SizeText = c.FileSize > 0 ? Fmt.Size(c.FileSize) : "—",
                StatusText = statusText,
                Url = c.Url,
                IsDuplicate = isDup,
                Selected = !isDup,
            });
        }

        UpdateSummary();
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows.Where(r => r.Selectable))
            r.Selected = true;
        UpdateSummary();
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows)
            r.Selected = false;
        UpdateSummary();
    }

    void RowSelection_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

    void UpdateSummary()
    {
        int total = _rows.Count;
        int selected = _rows.Count(r => r.Selected);
        int duplicates = _rows.Count(r => r.IsDuplicate);
        SummaryText.Text = Loc.T("idm.summary", total, selected, duplicates, _malformedSkipped);
        ImportBtn.IsEnabled = selected > 0;
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Selected && r.Selectable).Select(r => r.Candidate).ToList();
        if (chosen.Count == 0) return;

        Guid? targetQueueId = (QueueCombo.SelectedItem as QueueOption)?.Id;
        var summary = IdmImport.Materialize(chosen, _manager.Items, _manager.Settings, targetQueueId);
        _manager.ImportItems(summary.Imported, targetQueueId);

        App.Instance?.Notify(
            Loc.T("idm.title"),
            Loc.T("idm.result.imported", summary.Imported.Count, summary.DuplicateSkippedCount));
        DialogResult = true;
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
