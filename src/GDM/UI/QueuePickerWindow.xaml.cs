using System.Windows;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class QueuePickerWindow : Window
{
    readonly DownloadManager _manager;
    public Guid SelectedQueueId { get; private set; } = QueueDef.MainId;
    public bool StartNow { get; private set; }

    public QueuePickerWindow(DownloadManager manager, int count)
    {
        InitializeComponent();
        _manager = manager;
        TitleText.Text = Loc.T("win.queuepicker.header_format", count);
        QueueBox.ItemsSource = _manager.Settings.Queues;
        QueueBox.SelectedItem = _manager.Settings.MainQueue;
    }

    void NewQueue_Click(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, Loc.T("win.queuepicker.new_queue"), Loc.T("win.queuepicker.new_queue_prompt"), Loc.T("win.queuepicker.new_queue"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var q = new QueueDef { Name = name.Trim() };
        _manager.Settings.Queues.Add(q);
        _manager.Settings.Save();
        App.Instance.MainView?.RefreshCategories();
        QueueBox.Items.Refresh();
        QueueBox.SelectedItem = q;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (QueueBox.SelectedItem is not QueueDef q)
        {
            return;
        }
        SelectedQueueId = q.Id;
        StartNow = StartBox.IsChecked == true;
        if (DontAskBox.IsChecked == true)
        {
            _manager.Settings.ShowQueuePanelOnLater = false;
            _manager.Settings.Save();
        }
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
