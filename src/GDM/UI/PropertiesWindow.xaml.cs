using System.Windows;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class PropertiesWindow : Window
{
    readonly DownloadItem _item;
    readonly DownloadManager _manager;
    readonly bool _running;
    readonly bool _completed;
    readonly string _initialLogin;
    readonly string _initialPassword;

    public PropertiesWindow(DownloadItem item, DownloadManager manager)
    {
        InitializeComponent();
        _item = item;
        _manager = manager;
        _running = manager.IsRunning(item);
        _completed = item.Status == DownloadStatus.Completed;
        _initialLogin = item.Login;
        _initialPassword = item.Password;
        Glyph.Text = item.Glyph;
        NameBox.Text = item.FileName;
        TypeText.Text = item.Extension;
        StatusText.Text = item.StatusText;
        SizeText.Text = item.TotalSize > 0 ? Fmt.Size(item.TotalSize) : Fmt.Size(item.Downloaded);
        FolderBox.Text = item.SaveDir;
        // A data: address can be megabytes long and would freeze the layout; it is shown shortened and cannot be edited.
        _urlLocked = FileNames.IsDataUrl(item.Url);
        UrlBox.Text = _urlLocked ? item.DisplayUrl : item.Url;
        UrlBox.IsReadOnly = _urlLocked;
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

    public static void SanitizeCredentialsOnUrlChange(
        DownloadItem item,
        string oldUrl,
        string newUrl,
        string initialLogin,
        string initialPassword,
        ref string login,
        ref string password)
    {
        if (string.Equals(oldUrl, newUrl, StringComparison.Ordinal))
            return;

        bool oldOk = Uri.TryCreate(oldUrl, UriKind.Absolute, out var oldUri);
        bool newOk = Uri.TryCreate(newUrl, UriKind.Absolute, out var newUri);
        bool sameHost = oldOk && newOk && string.Equals(oldUri!.Host, newUri!.Host, StringComparison.OrdinalIgnoreCase);
        bool schemeDowngrade = oldOk && newOk
            && oldUri!.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !newUri!.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

        if (!sameHost || schemeDowngrade)
        {
            item.Cookies = "";
            item.CookieHost = "";
            bool loginUnchanged = string.Equals(login, initialLogin, StringComparison.Ordinal)
                                  && string.Equals(password, initialPassword, StringComparison.Ordinal);
            if (loginUnchanged || schemeDowngrade)
            {
                login = "";
                password = "";
            }
        }
    }

    readonly bool _urlLocked;

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var originalDir = _item.SaveDir;
        var originalTemp = _item.TempPath;
        var originalUrl = _item.Url;
        if (!_running && !_completed && string.IsNullOrWhiteSpace(NameBox.Text))
        {
            Confirm.Info(this, Loc.T("main.props.title"), Loc.T("win.props.err_empty_name"));
            return;
        }
        var expectedHash = ExpectedHashBox.Text.Trim();
        if (!string.IsNullOrEmpty(expectedHash) && !HashSpec.TryParse(expectedHash, out _))
        {
            Confirm.Info(this, Loc.T("main.props.title"), Loc.T("hash.win.invalid_hash"));
            return;
        }
        var loginVal = LoginBox.Text.Trim();
        var passwordVal = PasswordBox.Password;
        var newUrl = _urlLocked ? originalUrl : UrlBox.Text.Trim();
        bool urlChanged = !_running && !string.Equals(originalUrl, newUrl, StringComparison.Ordinal);
        if (urlChanged && !FileNames.IsDownloadableUrl(newUrl) && !Golan.Gdm.Core.Torrent.MagnetValidator.IsMagnetUri(newUrl))
        {
            Confirm.Info(this, Loc.T("main.props.title"), Loc.T("win.props.err_invalid_url"));
            return;
        }

        // The folder checks and file moves below can still refuse. The item's own fields are written only after they
        // pass, so an error followed by Cancel leaves the download exactly as it was (address, cookies, login).
        void ApplyFields()
        {
            if (!_running)
            {
                if (urlChanged)
                {
                    SanitizeCredentialsOnUrlChange(_item, originalUrl, newUrl, _initialLogin, _initialPassword, ref loginVal, ref passwordVal);
                    _item.Url = newUrl;
                    _item.ResolvedUrl = "";
                    _item.SanitizeMegaUrlIfNeeded();
                }
                _item.Referrer = ReferrerText.Text.Trim();
            }
            _item.Description = DescriptionBox.Text;
            _item.ExpectedHash = expectedHash;
            _item.AutoExtract = AutoExtractCheck.IsChecked;
            _item.Login = loginVal;
            _item.Password = passwordVal;
        }

        if (_completed && !string.Equals(_item.SaveDir, FolderBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var newDir = FolderBox.Text.Trim();
            // IsAllowedFolder already decides network shares: those under a folder the user configured are fine.
            if (!AddDownloadWindow.IsAllowedFolder(newDir, _manager.Settings))
            {
                Confirm.Info(this, Loc.T("main.props.moveFailedTitle"), Loc.T("main.add.invalidFolder"));
                return;
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
                Confirm.Info(this, Loc.T("main.props.moveFailedTitle"), FriendlyError.Format(ex));
                return;
            }
        }
        else if (!_running && !_completed)
        {
            var sanitized = FileNames.Sanitize(NameBox.Text);
            var newDir = FolderBox.Text.Trim();
            if (!AddDownloadWindow.IsAllowedFolder(newDir, _manager.Settings))
            {
                Confirm.Info(this, Loc.T("main.props.moveFailedTitle"), Loc.T("main.add.invalidFolder"));
                return;
            }
            try
            {
                Directory.CreateDirectory(newDir);
            }
            catch (Exception ex)
            {
                Log.Write("props folder create failed: " + ex.Message);
                Confirm.Info(this, Loc.T("main.props.moveFailedTitle"), FriendlyError.Format(ex));
                return;
            }
            var tempFolder = string.IsNullOrWhiteSpace(DownloadItem.TempRoot) ? newDir : DownloadItem.TempRoot;
            var newTemp = string.IsNullOrWhiteSpace(DownloadItem.TempRoot)
                ? Path.Combine(newDir, sanitized + ".gdm")
                : Path.Combine(DownloadItem.TempRoot, $"{_item.Id:N}.gdm");
            if (!string.Equals(originalTemp, newTemp, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(originalTemp)) _item.ActiveTempPath = "";
                try
                {
                    Directory.CreateDirectory(tempFolder);
                    if (File.Exists(originalTemp))
                    {
                        File.Move(originalTemp, newTemp, overwrite: false);
                        _item.ActiveTempPath = newTemp;
                    }
                    StreamDownloader.MoveSidecars(originalTemp, newTemp);
                }
                catch (Exception ex)
                {
                    Log.Write("props temp move failed: " + ex.Message);
                    Confirm.Info(this, Loc.T("main.props.moveFailedTitle"), FriendlyError.Format(ex));
                    return;
                }
            }
            if (!string.Equals(_item.FileName, sanitized, StringComparison.Ordinal))
            {
                _item.FileName = sanitized;
                _item.NameLocked = true;
            }
            _item.SaveDir = newDir;
            PathRegistry.Release(_item.Id);
            PathRegistry.Claim(_item.Id, _item.FullPath);
        }
        ApplyFields();
        _manager.Save();
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = true;
        else
            Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
