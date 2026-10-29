using System.Windows;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class CompleteWindow : Window
{
    readonly DownloadItem _item;

    public CompleteWindow(DownloadItem item)
    {
        InitializeComponent();
        _item = item;
        NameText.Text = item.FileName;
        NameText.FlowDirection = TextDir.IsRtl(item.FileName) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        NameText.HorizontalAlignment = HorizontalAlignment.Left;
        UrlText.Text = item.Url;
        PathText.Text = item.FullPath;
        SizeText.Text = Fmt.Size(item.TotalSize > 0 ? item.TotalSize : item.Downloaded);
        Closing += (_, _) => SavePreference();
    }

    void Open_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFile(_item);

    void OpenWith_Click(object sender, RoutedEventArgs e) => AfterDownload.OpenWith(_item.FullPath);

    void Folder_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(_item);

    void Close_Click(object sender, RoutedEventArgs e)
    {
        SavePreference();
        Close();
    }

    void SavePreference()
    {
        if (DontShow.IsChecked == true)
        {
            App.Instance.Settings.ShowCompleteDialog = false;
            App.Instance.Settings.Save();
        }
    }
}
