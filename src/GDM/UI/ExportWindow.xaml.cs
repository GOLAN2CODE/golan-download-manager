using System.Windows;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class ExportWindow : Window
{
    public string Scope { get; private set; } = "all";

    public ExportWindow(int selectedCount, int queueCount)
    {
        InitializeComponent();
        QueueRadio.Content = Loc.T("win.export.queue_format", queueCount);
        SelectedRadio.Content = Loc.T("win.export.selected_format", selectedCount);
        AllRadio.Content = Loc.T("win.export.all");
        SelectedRadio.IsEnabled = selectedCount > 0;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Scope = QueueRadio.IsChecked == true ? "queue" : SelectedRadio.IsChecked == true ? "selected" : "all";
        DialogResult = true;
    }
}
