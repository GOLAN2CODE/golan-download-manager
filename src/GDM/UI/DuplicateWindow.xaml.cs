using System.Windows;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class DuplicateWindow : Window
{
    readonly DownloadItem _existing;
    public string Choice { get; private set; } = "number";
    public bool Remember { get; private set; }
    public DuplicateWindow(DownloadItem existing, DownloadItem incoming)
    {
        InitializeComponent();
        _existing = existing;
        ExistingNameText.Text = existing.FileName;
        ExistingNameText.FlowDirection = TextDir.IsRtl(existing.FileName) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        ExistingNameText.ToolTip = existing.Url;
        ExistingStatusText.Text = existing.StatusText;
    }
    void Ok_Click(object sender, RoutedEventArgs e)
    {
        Choice = OverwriteRadio.IsChecked == true ? "overwrite" : ResumeRadio.IsChecked == true ? "resume" : "number";
        Remember = RememberBox.IsChecked == true;
        DialogResult = true;
        Close();
    }
}
