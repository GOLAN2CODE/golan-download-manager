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
        UrlText.Text = FileNames.IsDataUrl(item.Url) ? item.DisplayUrl : item.Url; // a huge data: address would freeze the layout
        PathText.Text = item.FullPath;
        SizeText.Text = Fmt.Size(item.TotalSize > 0 ? item.TotalSize : item.Downloaded);
        Closing += (_, _) => SavePreference();
    }

    void Open_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFile(_item);

    void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (!MainWindow.ConfirmPlainHttpExecutable(_item, this)) return;
        AfterDownload.OpenWith(_item.FullPath);
    }

    void Folder_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFolder(_item);

    void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    void SavePreference()
    {
        if (DontShow.IsChecked == true && App.Instance?.Settings is { } s)
        {
            s.ShowCompleteDialog = false;
            s.Save();
        }
    }
}
