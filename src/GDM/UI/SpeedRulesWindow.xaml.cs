using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class SpeedRulesWindow : Window
{
    public sealed class RuleRow : ObservableObject
    {
        bool _enabled = true;
        DaysMask _days = DaysMask.All;
        string _fromText = "00:00";
        string _toText = "06:00";
        int _limitKBps = 500;

        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

        public DaysMask Days
        {
            get => _days;
            set { if (Set(ref _days, value)) OnPropertyChanged(nameof(DaysPreset)); }
        }

        public DaysMask DaysPreset
        {
            get => _days;
            set => Days = value;
        }

        public string FromText
        {
            get => _fromText;
            set
            {
                if (TimeOnly.TryParse(value?.Trim(), CultureInfo.InvariantCulture, out var t))
                    Set(ref _fromText, t.ToString("HH:mm", CultureInfo.InvariantCulture));
            }
        }

        public string ToText
        {
            get => _toText;
            set
            {
                if (TimeOnly.TryParse(value?.Trim(), CultureInfo.InvariantCulture, out var t))
                    Set(ref _toText, t.ToString("HH:mm", CultureInfo.InvariantCulture));
            }
        }

        public int LimitKBps
        {
            get => _limitKBps;
            set => Set(ref _limitKBps, Math.Clamp(value, 0, 1024000));
        }

        public SpeedRule ToModel() => new()
        {
            Enabled = Enabled,
            Days = Days,
            From = TimeOnly.TryParse(FromText, CultureInfo.InvariantCulture, out var f) ? f : new TimeOnly(0, 0),
            To = TimeOnly.TryParse(ToText, CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(6, 0),
            LimitKBps = Math.Max(0, LimitKBps),
        };

        public static RuleRow FromModel(SpeedRule r) => new()
        {
            Enabled = r.Enabled,
            Days = r.Days,
            FromText = r.From.ToString("HH:mm", CultureInfo.InvariantCulture),
            ToText = r.To.ToString("HH:mm", CultureInfo.InvariantCulture),
            LimitKBps = r.LimitKBps,
        };
    }

    public sealed record DaysOption(DaysMask Key, string Label);

    readonly AppSettings _settings;
    readonly SpeedRuleController? _controller;
    readonly ObservableCollection<RuleRow> _rows = [];

    public SpeedRulesWindow(AppSettings settings, SpeedRuleController? controller = null)
    {
        InitializeComponent();
        _settings = settings;
        _controller = controller;

        DaysColumn.ItemsSource = new List<DaysOption>
        {
            new(DaysMask.All, Loc.T($"speedrules.win.days.all")),
            new(DaysMask.Weekdays, Loc.T($"speedrules.win.days.weekdays")),
            new(DaysMask.Weekend, Loc.T($"speedrules.win.days.weekend")),
            new(DaysMask.Sunday, Loc.Culture.DateTimeFormat.GetDayName(DayOfWeek.Sunday)),
            new(DaysMask.Monday, Loc.Culture.DateTimeFormat.GetDayName(DayOfWeek.Monday)),
            new(DaysMask.Tuesday, Loc.Culture.DateTimeFormat.GetDayName(DayOfWeek.Tuesday)),
            new(DaysMask.Wednesday, Loc.Culture.DateTimeFormat.GetDayName(DayOfWeek.Wednesday)),
            new(DaysMask.Thursday, Loc.Culture.DateTimeFormat.GetDayName(DayOfWeek.Thursday)),
            new(DaysMask.Friday, Loc.Culture.DateTimeFormat.GetDayName(DayOfWeek.Friday)),
            new(DaysMask.Saturday, Loc.Culture.DateTimeFormat.GetDayName(DayOfWeek.Saturday)),
        };

        foreach (var r in _settings.SpeedRules)
            _rows.Add(RuleRow.FromModel(r));
        RulesGrid.ItemsSource = _rows;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var row = new RuleRow();
        _rows.Add(row);
        RulesGrid.SelectedItem = row;
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is RuleRow row)
            _rows.Remove(row);
    }

    void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not RuleRow row) return;
        int idx = _rows.IndexOf(row);
        if (idx > 0)
        {
            _rows.Move(idx, idx - 1);
            RulesGrid.SelectedItem = row;
        }
    }

    void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not RuleRow row) return;
        int idx = _rows.IndexOf(row);
        if (idx >= 0 && idx < _rows.Count - 1)
        {
            _rows.Move(idx, idx + 1);
            RulesGrid.SelectedItem = row;
        }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        RulesGrid.CommitEdit();
        RulesGrid.CommitEdit();
        _settings.SpeedRules = _rows.Select(r => r.ToModel()).ToList();
        _settings.Save();
        _controller?.OnSettingsChanged();
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
