using System.Windows;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class PropertiesWindow : Window
{
    readonly DownloadItem _item;
    readonly DownloadManager _manager;
    readonly bool _running;
    readonly bool _completed;

    public PropertiesWindow(DownloadItem item, DownloadManager manager)
    {
        InitializeComponent();
        _item = item;
        _manager = manager;
        _running = manager.IsRunning(item);
        _completed = item.Status == DownloadStatus.Completed;
        Glyph.Text = item.Glyph;
        NameBox.Text = item.FileName;
        TypeText.Text = item.Extension;
        StatusText.Text = item.StatusText;
        SizeText.Text = item.TotalSize > 0 ? Fmt.Size(item.TotalSize) : Fmt.Size(item.Downloaded);
        FolderBox.Text = item.SaveDir;
        UrlBox.Text = item.Url;
        DescriptionBox.Text = item.Description;
        ReferrerText.Text = item.Referrer;
        ParentText.Text = item.ParentPage;
        LoginBox.Text = item.Login;
        PasswordBox.Password = item.Password;
        AddedText.Text = item.AddedAt.ToString("g");
        LastTryText.Text = item.LastTry?.ToString("g") ?? "—";
        CompletedText.Text = item.CompletedAt?.ToString("g") ?? "—";
        ResultText.Text = string.IsNullOrWhiteSpace(item.LastResult) ? item.ErrorMessage : item.LastResult;
        QueueText.Text = manager.Settings.FindQueue(item.QueueId)?.Name ?? Loc.T("main.props.noQueue");
        ExpectedHashBox.Text = item.ExpectedHash;
        AutoExtractCheck.IsChecked = item.AutoExtract;
        NameBox.IsEnabled = !_running && !_completed;
        FolderBox.IsEnabled = !_running;
        BrowseButton.IsEnabled = !_running;
        UrlBox.IsEnabled = !_running;
        ReferrerText.IsEnabled = !_running;
        OpenButton.Visibility = _completed ? Visibility.Visible : Visibility.Collapsed;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var initialDir = Directory.Exists(FolderBox.Text)
            ? FolderBox.Text
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _completed ? Loc.T("main.props.browseMoveTitle") : Loc.T("main.props.browseSaveTitle"),
            InitialDirectory = initialDir
        };
        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
        }
    }

    void Open_Click(object sender, RoutedEventArgs e) => MainWindow.OpenFile(_item);

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var originalDir = _item.SaveDir;
        var originalTemp = _item.TempPath;
        if (!_running)
        {
            var newUrl = UrlBox.Text.Trim();
            if (FileNames.IsHttpUrl(newUrl) || Golan.Gdm.Core.Torrent.MagnetValidator.IsMagnetUri(newUrl))
            {
                _item.Url = newUrl;
                _item.SanitizeMegaUrlIfNeeded();
            }
            _item.Referrer = ReferrerText.Text.Trim();
        }
        _item.Description = DescriptionBox.Text;
        _item.ExpectedHash = ExpectedHashBox.Text.Trim();
        _item.AutoExtract = AutoExtractCheck.IsChecked;
        _item.Login = LoginBox.Text;
        _item.Password = PasswordBox.Password;
        if (_completed && !string.Equals(_item.SaveDir, FolderBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var newDir = FolderBox.Text.Trim();
            bool isUnc = newDir.StartsWith(@"\\", StringComparison.Ordinal) ||
                         (Uri.TryCreate(newDir, UriKind.Absolute, out var u) && u.IsUnc);
            if (isUnc || !AddDownloadWindow.IsAllowedFolder(newDir, _manager.Settings))
            {
                if (!AddDownloadWindow.IsAllowedFolder(newDir, _manager.Settings))
                {
                    Confirm.Info(this, Loc.T("main.props.moveFailedTitle"), Loc.T("main.add.invalidFolder"));
                    return;
                }
            }
            var target = Path.Combine(newDir, _item.FileName);
            try
            {
                Directory.CreateDirectory(newDir);
                File.Move(Path.Combine(originalDir, _item.FileName), target, overwrite: false);
                _item.SaveDir = newDir;
                PathRegistry.Release(_item.Id);
                PathRegistry.Claim(_item.Id, _item.FullPath);
            }
            catch (Exception ex)
            {
                Confirm.Info(this, Loc.T("main.props.moveFailedTitle"), ex.Message);
                return;
            }
        }
        else if (!_running && !_completed)
        {
            var sanitized = FileNames.Sanitize(NameBox.Text);
            var newDir = FolderBox.Text.Trim();
            string targetDir = _item.SaveDir;
            bool validNewDir = AddDownloadWindow.IsAllowedFolder(newDir, _manager.Settings);
            if (validNewDir)
            {
                try
                {
                    Directory.CreateDirectory(newDir);
                    targetDir = newDir;
                }
                catch (Exception ex)
                {
                    Log.Write("props folder create failed: " + ex.Message);
                    validNewDir = false;
                }
            }
            var tempFolder = string.IsNullOrWhiteSpace(DownloadItem.TempRoot) ? targetDir : DownloadItem.TempRoot;
            var newTemp = string.IsNullOrWhiteSpace(DownloadItem.TempRoot)
                ? Path.Combine(targetDir, sanitized + ".gdm")
                : Path.Combine(DownloadItem.TempRoot, $"{_item.Id:N}.gdm");
            if (!string.Equals(originalTemp, newTemp, StringComparison.OrdinalIgnoreCase) && !File.Exists(originalTemp)) _item.ActiveTempPath = "";
            if (!string.Equals(originalTemp, newTemp, StringComparison.OrdinalIgnoreCase) && File.Exists(originalTemp))
            {
                try
                {
                    Directory.CreateDirectory(tempFolder);
                    File.Move(originalTemp, newTemp, overwrite: true);
                    _item.ActiveTempPath = newTemp;
                }
                catch (Exception ex)
                {
                    Log.Write("props temp move failed: " + ex.Message);
                }
            }
            if (!string.Equals(_item.FileName, sanitized, StringComparison.Ordinal))
            {
                _item.FileName = sanitized;
                _item.NameLocked = true;
            }
            if (validNewDir)
            {
                _item.SaveDir = newDir;
            }
            PathRegistry.Release(_item.Id);
            PathRegistry.Claim(_item.Id, _item.FullPath);
        }
        _manager.Save();
        DialogResult = true;
        Close();
    }
}
