using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class StatsWindow : Window
{
    public sealed record StatRow(string Name, string SizeText);
    public sealed record DailyBarItem(string Label, double BarHeight, string Tooltip);

    readonly StatsStore _store;
    StatsRange _range = StatsRange.Last30Days;

    public StatsWindow(StatsStore store)
    {
        InitializeComponent();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        LoadRange(StatsRange.Last30Days);
    }

    void Range_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        _range = Enum.TryParse<StatsRange>(tag, out var parsed) ? parsed : StatsRange.Last30Days;

        BtnToday.IsChecked = _range == StatsRange.Today;
        Btn7Days.IsChecked = _range == StatsRange.Last7Days;
        Btn30Days.IsChecked = _range == StatsRange.Last30Days;
        BtnAll.IsChecked = _range == StatsRange.AllTime;

        LoadRange(_range);
    }

    void LoadRange(StatsRange range)
    {
        var summary = _store.Query(range);
        TotalBytesText.Text = Fmt.Size(summary.TotalBytes);
        TotalFilesText.Text = summary.TotalFiles.ToString("N0", Loc.Culture);
        AvgSpeedText.Text = summary.AverageSpeedBytesPerSec > 0
            ? Fmt.Speed(summary.AverageSpeedBytesPerSec)
            : "0 " + Loc.T("core.unit.b") + Loc.T("core.unit.per_sec");
        ActiveTimeText.Text = summary.ActiveSeconds > 0
            ? Fmt.Duration(summary.ActiveSeconds)
            : "0:00";

        CategoriesGrid.ItemsSource = summary.ByCategory
            .Select(c => new StatRow(Categories.Get(c.Key).Name, Fmt.Size(c.Bytes)))
            .ToList();

        HostsGrid.ItemsSource = summary.TopHosts
            .Select(h => new StatRow(h.Host, Fmt.Size(h.Bytes)))
            .ToList();

        long maxDayBytes = summary.DailySeries.Select(d => d.Bytes).DefaultIfEmpty(0).Max();
        double scale = maxDayBytes > 0 ? 85.0 / maxDayBytes : 0;

        DailyBarsControl.ItemsSource = summary.DailySeries
            .TakeLast(60)
            .Select(d => new DailyBarItem(
                d.Date.ToString("MM/dd", CultureInfo.InvariantCulture),
                Math.Max(4.0, d.Bytes * scale),
                $"{d.Date:yyyy-MM-dd}: {Fmt.Size(d.Bytes)} ({d.Files})"))
            .ToList();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
