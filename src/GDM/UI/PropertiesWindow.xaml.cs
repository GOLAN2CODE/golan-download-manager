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
        CategoryBox.ItemsSource = Categories.All;
        CategoryBox.SelectedItem = Categories.Get(item.Category);
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
        NameBox.IsEnabled = !_running;
        FolderBox.IsEnabled = !_running;
        BrowseButton.IsEnabled = !_running;
        UrlBox.IsEnabled = !_running;
        ReferrerText.IsEnabled = !_running;
        OpenButton.Visibility = _completed ? Visibility.Visible : Visibility.Collapsed;
    }

    void CategoryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is CategoryDef cat)
            Glyph.Text = cat.Glyph;
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
        var originalUrl = _item.Url;
        if (!_running && string.IsNullOrWhiteSpace(NameBox.Text))
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

        if (!_running)
        {
            var newDir = FolderBox.Text.Trim();
            if (!AddDownloadWindow.IsAllowedFolder(newDir, _manager.Settings))
            {
                Confirm.Info(this, Loc.T("main.props.moveFailedTitle"), Loc.T("main.add.invalidFolder"));
                return;
            }
            bool nameChanged = !string.Equals(_item.FileName, NameBox.Text.Trim(), StringComparison.Ordinal);
            bool dirChanged = !string.Equals(_item.SaveDir, newDir, StringComparison.OrdinalIgnoreCase);
            if (nameChanged || dirChanged)
            {
                if (!_manager.TryRenameItem(_item, NameBox.Text, out var renameErr, targetDirOverride: newDir))
                {
                    Confirm.Info(this, Loc.T("main.props.title"), renameErr);
                    return;
                }
            }
        }

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

        if (CategoryBox.SelectedItem is CategoryDef selectedCat && !string.Equals(_item.Category, selectedCat.Key, StringComparison.Ordinal))
        {
            _manager.ChangeCategory(_item, selectedCat.Key, save: false);
        }

        _item.Description = DescriptionBox.Text;
        _item.ExpectedHash = expectedHash;
        _item.AutoExtract = AutoExtractCheck.IsChecked;
        _item.Login = loginVal;
        _item.Password = passwordVal;
        _manager.Save();
        App.Instance?.MainView?.RefreshCategoriesAndList();
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            DialogResult = true;
        else
            Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
