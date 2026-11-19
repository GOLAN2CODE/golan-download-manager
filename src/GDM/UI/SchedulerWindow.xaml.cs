using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Golan.Gdm.Core;
using Microsoft.Win32;

namespace Golan.Gdm.UI;

public partial class SchedulerWindow : Window
{
    readonly DownloadManager _manager;
    QueueDef? _queue;
    bool _loading;

    public SchedulerWindow(DownloadManager manager, Guid? selectQueue)
    {
        InitializeComponent();
        _manager = manager;
        QueuesList.ItemsSource = _manager.Settings.Queues;
        _manager.Ticked += Manager_Ticked;
        Closed += (_, _) => _manager.Ticked -= Manager_Ticked;
        Loaded += (_, _) =>
        {
            QueuesList.SelectedItem = _manager.Settings.FindQueue(selectQueue) ?? _manager.Settings.MainQueue;
        };
    }

    void Manager_Ticked()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshItems); return; }
        RefreshItems();
    }

    void QueuesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && _queue != null)
        {
            SyncQueueFromControls();
        }
        if (QueuesList.SelectedItem is QueueDef q)
        {
            _queue = q;
            LoadQueue();
        }
    }

    void LoadQueue()
    {
        if (_queue == null) return;
        _loading = true;
        DeleteQueueBtn.IsEnabled = !_queue.BuiltIn;
        QueueNameBox.IsReadOnly = _queue.BuiltIn;
        QueueNameBox.Text = _queue.Name;
        QueueError.Text = "";
        ScheduleEnabledBox.IsChecked = _queue.ScheduleEnabled;
        PeriodicBox.IsChecked = _queue.Periodic;
        StartTimeBox.Text = _queue.StartTime;
        OnceRadio.IsChecked = !_queue.Daily;
        DailyRadio.IsChecked = _queue.Daily;
        OnceDatePicker.SelectedDate = _queue.OnceDate ?? DateTime.Today;
        foreach (var box in DaysPanel.Children.OfType<CheckBox>())
            box.IsChecked = Enum.TryParse<DayOfWeek>(box.Tag?.ToString(), out var day) && _queue.Days.Contains(day);
        RepeatBox.IsChecked = _queue.RepeatEnabled;
        RepeatHoursBox.Text = _queue.RepeatHours.ToString();
        RepeatMinutesBox.Text = _queue.RepeatMinutes.ToString();
        StopBox.IsChecked = _queue.StopEnabled;
        StopTimeBox.Text = _queue.StopTime;
        StartupBox.IsChecked = _queue.StartOnStartup;
        RetriesBox.Text = _queue.Retries.ToString();
        OpenWhenDoneBox.Text = _queue.OpenWhenDone;
        ExitBox.IsChecked = _queue.ExitWhenDone;
        SleepBox.IsChecked = _queue.SleepWhenDone;
        HibernateBox.IsChecked = _queue.HibernateWhenDone;
        ShutdownBox.IsChecked = _queue.ShutdownWhenDone;
        ForceBox.IsChecked = _queue.ForceProcesses;
        ForceBox.IsEnabled = ShutdownBox.IsChecked == true;
        MaxSimultaneousBox.Text = _queue.MaxSimultaneous.ToString();
        _loading = false;
        RefreshItems();
        ValidateTimes();
        UpdateNextStart();
    }

    void RefreshItems()
    {
        if (_queue == null) return;
        var selectedId = (QueueItemsGrid.SelectedItem as DownloadItem)?.Id;
        var items = _manager.QueueItems(_queue.Id).ToList();
        QueueItemsGrid.ItemsSource = items;
        QueueEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selectedId is { } id)
            QueueItemsGrid.SelectedItem = items.FirstOrDefault(x => x.Id == id);
        UpdateNextStart();
        UpdateQueueRunningTags();
    }

    void UpdateQueueRunningTags()
    {
        if (QueuesList.Items.Count == 0) return;
        foreach (var item in QueuesList.Items)
            if (QueuesList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
                container.Tag = (Brush)(FindResource(_manager.IsQueueRunning(((QueueDef)item).Id) ? "Success" : "Muted"));
    }

    void QueueItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem item && item.DataContext is QueueDef queue)
            item.Tag = (Brush)(FindResource(_manager.IsQueueRunning(queue.Id) ? "Success" : "Muted"));
    }

    void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, Loc.T("win.scheduler.new_queue_title"), Loc.T("win.scheduler.new_queue_prompt"), Loc.T("win.scheduler.new_queue"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var q = new QueueDef { Name = name.Trim() };
        _manager.Settings.Queues.Add(q);
        QueuesList.Items.Refresh();
        QueuesList.SelectedItem = q;
    }

    void DeleteQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_queue == null || _queue.BuiltIn) return;
        if (!Confirm.Ask(this, Loc.T("win.scheduler.delete_title"), Loc.T("win.scheduler.delete_confirm", _queue.Name), Loc.T("win.scheduler.delete"), true)) return;
        var toDelete = _queue;
        _manager.StopQueue(toDelete.Id);
        foreach (var item in _manager.QueueItems(toDelete.Id).ToList()) _manager.RemoveFromQueue(item);
        var index = _manager.Settings.Queues.IndexOf(toDelete);
        _manager.Settings.Queues.Remove(toDelete);
        _manager.Settings.Save();
        App.Instance?.MainView?.RefreshCategories();
        App.Instance?.RefreshTrayMenu();
        QueuesList.Items.Refresh();
        QueuesList.SelectedIndex = Math.Max(0, index - 1);
    }

    void QueueNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _queue == null || _queue.BuiltIn) return;
        var trimmed = QueueNameBox.Text.Trim();
        if (trimmed.Length == 0)
        {
            QueueError.Text = Loc.T("win.scheduler.err_empty_name");
            return;
        }
        QueueError.Text = "";
        _queue.Name = trimmed;
        QueuesList.Items.Refresh();
    }

    void ScheduleChanged_Click(object sender, RoutedEventArgs e)
    {
        if (!_loading && _queue != null) { _queue.ScheduleEnabled = ScheduleEnabledBox.IsChecked == true; UpdateNextStart(); }
    }

    void ScheduleType_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _queue == null) return;
        _queue.Daily = DailyRadio.IsChecked == true;
        _queue.OnceDate = OnceDatePicker.SelectedDate ?? DateTime.Today;
        UpdateNextStart();
    }

    void PowerOption_Click(object sender, RoutedEventArgs e)
    {
        if (!_loading && sender is CheckBox { IsChecked: true } chosen)
        {
            _loading = true;
            if (chosen != SleepBox) SleepBox.IsChecked = false;
            if (chosen != HibernateBox) HibernateBox.IsChecked = false;
            if (chosen != ShutdownBox) ShutdownBox.IsChecked = false;
            _loading = false;
        }
        ForceBox.IsEnabled = ShutdownBox.IsChecked == true;
        SyncQueueFromControls();
    }

    void QueueOptionChanged_Click(object sender, RoutedEventArgs e) => SyncQueueFromControls();
    void QueueOptionChanged_SelectedDateChanged(object sender, SelectionChangedEventArgs e) => SyncQueueFromControls();
    void QueueControl_LostFocus(object sender, RoutedEventArgs e) => SyncQueueFromControls();

    public static bool IsValidClockTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains(':'))
            return false;
        return TimeOnly.TryParseExact(
            text.Trim(),
            ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss"],
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out _);
    }

    void TimeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidateTimes();
        if (_loading || _queue == null) return;
        if (sender == StartTimeBox && IsValidClockTime(StartTimeBox.Text))
            _queue.StartTime = StartTimeBox.Text.Trim();
        else if (sender == StopTimeBox && IsValidClockTime(StopTimeBox.Text))
            _queue.StopTime = StopTimeBox.Text.Trim();
        UpdateNextStart();
    }

    void ValidateTimes()
    {
        TimeError.Text = IsValidClockTime(StartTimeBox.Text) ? "" : Loc.T("win.scheduler.err_start_time");
        if (StopBox.IsChecked == true && !IsValidClockTime(StopTimeBox.Text)) TimeError.Text = Loc.T("win.scheduler.err_stop_time");
    }

    void UpdateNextStart()
    {
        if (_queue == null) return;
        var next = QueueScheduler.NextStart(_queue, DateTime.Now);
        NextStartText.Text = next is { } value ? Loc.T("win.scheduler.next_start", value.ToString("yyyy/MM/dd HH:mm")) : Loc.T("win.scheduler.next_start_none");
    }

    void SyncQueueFromControls()
    {
        if (_loading || _queue == null) return;
        _queue.ScheduleEnabled = ScheduleEnabledBox.IsChecked == true;
        _queue.Periodic = PeriodicBox.IsChecked == true;
        if (IsValidClockTime(StartTimeBox.Text)) _queue.StartTime = StartTimeBox.Text.Trim();
        if (IsValidClockTime(StopTimeBox.Text)) _queue.StopTime = StopTimeBox.Text.Trim();
        _queue.OnceDate = OnceDatePicker.SelectedDate;
        _queue.Daily = DailyRadio.IsChecked == true;
        _queue.Days = DaysPanel.Children.OfType<CheckBox>()
            .Where(b => b.IsChecked == true && Enum.TryParse<DayOfWeek>(b.Tag?.ToString(), out _))
            .Select(b => Enum.Parse<DayOfWeek>(b.Tag!.ToString()!)).ToList();
        _queue.RepeatEnabled = RepeatBox.IsChecked == true;
        if (int.TryParse(RepeatHoursBox.Text, out var hours)) _queue.RepeatHours = Math.Max(0, hours);
        if (int.TryParse(RepeatMinutesBox.Text, out var minutes)) _queue.RepeatMinutes = Math.Clamp(minutes, 0, 59);
        _queue.StopEnabled = StopBox.IsChecked == true;
        _queue.StartOnStartup = StartupBox.IsChecked == true;
        if (int.TryParse(RetriesBox.Text, out var retries)) _queue.Retries = Math.Max(0, retries);
        _queue.OpenWhenDone = OpenWhenDoneBox.Text.Trim();
        _queue.ExitWhenDone = ExitBox.IsChecked == true;
        _queue.SleepWhenDone = SleepBox.IsChecked == true;
        _queue.HibernateWhenDone = HibernateBox.IsChecked == true;
        _queue.ShutdownWhenDone = ShutdownBox.IsChecked == true;
        _queue.ForceProcesses = ForceBox.IsChecked == true;
        ForceBox.IsEnabled = ShutdownBox.IsChecked == true;
        if (int.TryParse(MaxSimultaneousBox.Text, out var max)) _queue.MaxSimultaneous = Math.Clamp(max, 1, 10);
        ValidateTimes();
        UpdateNextStart();
    }

    void MaxSimultaneous_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && _queue != null && int.TryParse(MaxSimultaneousBox.Text, out var value)) _queue.MaxSimultaneous = Math.Clamp(value, 1, 10);
    }

    void BrowseOpen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = Loc.T("win.scheduler.browse_open_title") };
        if (dialog.ShowDialog(this) == true)
        {
            OpenWhenDoneBox.Text = dialog.FileName;
            SyncQueueFromControls();
        }
    }

    DownloadItem? SelectedItem => QueueItemsGrid.SelectedItem as DownloadItem;
    void MoveUp_Click(object sender, RoutedEventArgs e) { if (SelectedItem is { } item) { _manager.MoveInQueue(item, -1); RefreshItems(); } }
    void MoveDown_Click(object sender, RoutedEventArgs e) { if (SelectedItem is { } item) { _manager.MoveInQueue(item, 1); RefreshItems(); } }
    void RemoveItem_Click(object sender, RoutedEventArgs e) { if (SelectedItem is { } item) { _manager.RemoveFromQueue(item); RefreshItems(); } }
    void Start_Click(object sender, RoutedEventArgs e) { SyncQueueFromControls(); if (_queue != null) _manager.StartQueue(_queue.Id); }
    void Stop_Click(object sender, RoutedEventArgs e) { if (_queue != null) _manager.StopQueue(_queue.Id); }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        SyncQueueFromControls();
        if (!ValidateTimesAndShow()) return;
        _manager.Settings.Save();
        App.Instance?.MainView?.RefreshCategories();
        App.Instance?.RefreshTrayMenu();
    }

    bool ValidateTimesAndShow()
    {
        ValidateTimes();
        if (!string.IsNullOrEmpty(TimeError.Text))
        {
            QueueTabs.SelectedIndex = 0;
            return false;
        }
        return true;
    }

    void Close_Click(object sender, RoutedEventArgs e) { Apply_Click(sender, e); if (string.IsNullOrEmpty(TimeError.Text)) Close(); }
}
