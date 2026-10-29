using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class GrabberWindow : Window
{
    readonly DownloadManager _manager;
    readonly GrabberProject _project = new();
    readonly ObservableCollection<GrabbedFile> _files = [];
    CancellationTokenSource _crawlCts = new();
    readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    SiteGrabber? _grabber;
    int _step = 1;

    public GrabberWindow(DownloadManager manager, string? startUrl)
    {
        InitializeComponent();
        _manager = manager;
        StartUrlBox.Text = startUrl ?? "";
        PresetBox.ItemsSource = GrabberProject.Presets.Select(x => new PresetItem(LocalizedPresetName(x.Mask, x.Name), x.Mask)).ToList();
        CategoryBox.ItemsSource = Categories.All;
        CategoryBox.SelectedItem = Categories.Get("general");
        ExploreAtOnceBox.ItemsSource = Enumerable.Range(1, 10).ToList();
        DownloadAtOnceBox.ItemsSource = Enumerable.Range(1, 10).ToList();
        ExploreAtOnceBox.SelectedItem = 4;
        DownloadAtOnceBox.SelectedItem = 4;
        FilesGrid.ItemsSource = _files;
        LoadDefaults();
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();
        Closed += (_, _) => _crawlCts.Cancel();
        Closed += (_, _) => _statsTimer.Stop();
    }

    sealed record PresetItem(string Name, string Mask);

    static string LocalizedPresetName(string mask, string fallback) => mask switch
    {
        "*.jpg,*.jpeg,*.png,*.gif,*.webp,*.bmp,*.svg" => Loc.T("win.grabber.preset.images"),
        "*.mp4,*.mkv,*.avi,*.mov,*.wmv,*.flv,*.webm,*.m4v" => Loc.T("win.grabber.preset.video"),
        "*.mp3,*.wav,*.m4a,*.aac,*.flac,*.ogg,*.wma" => Loc.T("win.grabber.preset.audio"),
        "*.pdf,*.doc,*.docx,*.xls,*.xlsx,*.ppt,*.pptx,*.txt,*.epub" => Loc.T("win.grabber.preset.documents"),
        "*.zip,*.rar,*.7z,*.tar,*.gz,*.iso" => Loc.T("win.grabber.preset.archives"),
        "*.exe,*.msi,*.apk,*.dmg" => Loc.T("win.grabber.preset.programs"),
        "*.*" => Loc.T("win.grabber.preset.all"),
        _ => fallback
    };

    void LoadDefaults()
    {
        LevelsBaseBox.Text = "2";
        LevelsOtherBox.Text = "0";
        MaxPagesBox.Text = "2000";
        MinSizeBox.Text = "0";
        MaxSizeBox.Text = "0";
        DuplicatesBox.IsChecked = true;
        DontParentBox.IsChecked = true;
        DescriptionBox.IsChecked = true;
        SavedBox.ItemsSource = GrabberProject.List().Select(n => new PresetItem(n, n)).ToList();
        foreach (var p in GrabberProject.Presets)
        {
            var chip = new ToggleButton
            {
                Content = LocalizedPresetName(p.Mask, p.Name),
                Style = (Style)FindResource("Chip"),
                Margin = new Thickness(0, 0, 6, 6),
                Tag = p.Mask
            };
            chip.Click += (_, _) => MasksBox.Text = (string)chip.Tag;
            PresetChips.Children.Add(chip);
        }
    }

    void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedItem is PresetItem p)
        {
            MasksBox.Text = p.Mask;
        }
    }

    void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        if (SavedBox.SelectedItem is not PresetItem saved || GrabberProject.Load(saved.Name) is not { } loaded)
        {
            return;
        }
        CopyFrom(loaded);
    }

    void CopyFrom(GrabberProject p)
    {
        NameBox.Text = p.Name;
        StartUrlBox.Text = p.StartUrl;
        LoginBox.Text = p.Login;
        PasswordBox.Password = p.Password;
        ExploreWholeBox.IsChecked = p.ExploreWholeSite;
        LevelsBaseBox.Text = GrabberProject.ClampCrawlDepth(p.LevelsBase).ToString();
        LevelsOtherBox.Text = GrabberProject.ClampCrawlDepth(p.LevelsOther).ToString();
        DontParentBox.IsChecked = p.DontExploreParent;
        MainDomainBox.IsChecked = p.ExploreMainDomain;
        ExploreOnlyBox.Text = p.ExploreOnly;
        ExploreSkipBox.Text = p.ExploreSkip;
        MaxPagesBox.Text = p.MaxPages.ToString();
        MasksBox.Text = p.FileMasks;
        SkipMasksBox.Text = p.FileSkipMasks;
        SiteOnlyBox.IsChecked = p.SearchThisSiteOnly;
        DuplicatesBox.IsChecked = p.HideDuplicates;
        MinSizeBox.Text = p.MinSizeKB.ToString();
        MaxSizeBox.Text = p.MaxSizeKB.ToString();
        FilesOnlyBox.Text = p.FilesOnly;
        FilesSkipBox.Text = p.FilesSkip;
        AllAtOnceBox.IsChecked = p.DownloadAllAtOnce;
        CategoryRadio.IsChecked = p.SaveMode == "category";
        OneCategoryRadio.IsChecked = p.SaveMode == "onecategory";
        FolderRadio.IsChecked = p.SaveMode == "folder";
        CategoryBox.SelectedItem = Categories.Get(p.SaveCategory);
        FolderBox.Text = Transfer.IsSafeImportFolder(p.SaveFolder, _manager.Settings, out var safeFolder)
            ? safeFolder
            : (AddDownloadWindow.IsAllowedFolder(p.SaveFolder, _manager.Settings) ? p.SaveFolder : _manager.Settings.FolderFor("general"));
        RelativeBox.IsChecked = p.KeepRelativeFolders;
        DescriptionBox.IsChecked = p.LinkTextAsDescription;
        ExploreAtOnceBox.SelectedItem = p.ExploreAtOnce;
        DownloadAtOnceBox.SelectedItem = p.DownloadAtOnce;
    }

    internal static int ClampCrawlDepth(int depth) => GrabberProject.ClampCrawlDepth(depth);

    void CopyToProject()
    {
        _project.Name = NameBox.Text.Trim();
        _project.StartUrl = StartUrlBox.Text.Trim();
        _project.Login = LoginBox.Text.Trim();
        _project.Password = PasswordBox.Password;
        _project.ExploreWholeSite = ExploreWholeBox.IsChecked == true;
        _project.LevelsBase = GrabberProject.ClampCrawlDepth(Int(LevelsBaseBox, 2));
        _project.LevelsOther = GrabberProject.ClampCrawlDepth(Int(LevelsOtherBox, 0));
        _project.DontExploreParent = DontParentBox.IsChecked == true;
        _project.ExploreMainDomain = MainDomainBox.IsChecked == true;
        _project.ExploreOnly = ExploreOnlyBox.Text.Trim();
        _project.ExploreSkip = ExploreSkipBox.Text.Trim();
        _project.MaxPages = Math.Clamp(Int(MaxPagesBox, 2000), 1, 10000);
        _project.FileMasks = MasksBox.Text.Trim();
        _project.FileSkipMasks = SkipMasksBox.Text.Trim();
        _project.SearchThisSiteOnly = SiteOnlyBox.IsChecked == true;
        _project.HideDuplicates = DuplicatesBox.IsChecked == true;
        _project.MinSizeKB = Long(MinSizeBox);
        _project.MaxSizeKB = Long(MaxSizeBox);
        _project.FilesOnly = FilesOnlyBox.Text.Trim();
        _project.FilesSkip = FilesSkipBox.Text.Trim();
        _project.DownloadAllAtOnce = AllAtOnceBox.IsChecked == true;
        _project.SaveMode = CategoryRadio.IsChecked == true
            ? "category"
            : OneCategoryRadio.IsChecked == true ? "onecategory" : "folder";
        _project.SaveCategory = (CategoryBox.SelectedItem as CategoryDef)?.Key ?? "general";
        _project.SaveFolder = FolderBox.Text.Trim();
        _project.KeepRelativeFolders = RelativeBox.IsChecked == true;
        _project.LinkTextAsDescription = DescriptionBox.IsChecked == true;
        _project.ExploreAtOnce = Math.Clamp(Int(ExploreAtOnceBox.SelectedItem, 4), 1, 10);
        _project.DownloadAtOnce = Math.Clamp(Int(DownloadAtOnceBox.SelectedItem, 4), 1, 10);
    }

    static int Int(object? value, int fallback) => int.TryParse(value?.ToString(), out var n) ? Math.Max(0, n) : fallback;

    static long Long(TextBox box) => long.TryParse(box.Text, out var n) ? Math.Max(0, n) : 0;

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 4)
        {
            _step++;
            ShowStep();
        }
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsView.Visibility == Visibility.Visible)
        {
            _crawlCts.Cancel();
            ResultsView.Visibility = Visibility.Collapsed;
            WizardScroll.Visibility = Visibility.Visible;
            ShowStep();
            return;
        }
        if (_step > 1)
        {
            _step--;
            ShowStep();
        }
    }

    void ShowStep()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        StepTitle.Text = new[]
        {
            "",
            Loc.T("win.grabber.step1_title"),
            Loc.T("win.grabber.step2_title"),
            Loc.T("win.grabber.step3_title"),
            Loc.T("win.grabber.step4_title")
        }[_step];
        var hints = new[]
        {
            "",
            Loc.T("win.grabber.step1_hint"),
            Loc.T("win.grabber.step2_hint"),
            Loc.T("win.grabber.step3_hint"),
            Loc.T("win.grabber.step4_hint")
        };
        StepHint.Text = hints[_step];
        BackButton.Visibility = Visibility.Visible;
        BackButton.IsEnabled = _step > 1;
        NextButton.Visibility = _step == 4 ? Visibility.Collapsed : Visibility.Visible;
        StartButton.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        var dots = new[] { Dot1, Dot2, Dot3, Dot4 };
        for (var i = 0; i < dots.Length; i++)
        {
            bool active = i + 1 <= _step;
            dots[i].Background = (Brush)FindResource(active ? "Accent" : "Line");
            dots[i].Foreground = (Brush)FindResource(active ? "AccentInk" : "Muted");
        }
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("win.grabber.browse_title") };
        if (Directory.Exists(FolderBox.Text))
        {
            dlg.InitialDirectory = FolderBox.Text;
        }
        if (dlg.ShowDialog(this) == true)
        {
            FolderBox.Text = dlg.FolderName;
        }
    }

    void Start_Click(object sender, RoutedEventArgs e)
    {
        CopyToProject();
        if (string.IsNullOrWhiteSpace(_project.Name))
        {
            ShowError(Loc.T("win.grabber.err.no_name"));
            return;
        }
        if (!FileNames.IsHttpUrl(_project.StartUrl))
        {
            ShowError(Loc.T("win.grabber.err.invalid_url"));
            return;
        }
        if (_project.SaveMode == "folder" && string.IsNullOrWhiteSpace(_project.SaveFolder))
        {
            ShowError(Loc.T("win.grabber.err.no_folder"));
            return;
        }
        try
        {
            _project.Save();
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("win.grabber.err.save_failed", ex.Message));
            return;
        }
        _crawlCts.Cancel();
        _crawlCts = new CancellationTokenSource();
        _files.Clear();
        FoundTree.Items.Clear();
        StopButton.IsEnabled = true;
        _grabber = new SiteGrabber(_project);
        _grabber.FileFound += file => Dispatcher.BeginInvoke(() => AddFile(file));
        WizardScroll.Visibility = Visibility.Collapsed;
        ResultsView.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        BackButton.IsEnabled = true;
        StartButton.Visibility = Visibility.Collapsed;
        _ = RunGrabberAsync(_grabber);
    }

    void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        CopyToProject();
        if (string.IsNullOrWhiteSpace(_project.Name))
        {
            ShowError(Loc.T("win.grabber.err.no_name"));
            return;
        }
        try
        {
            _project.Save();
            ValidationText.Text = Loc.T("win.grabber.project_saved");
            ValidationText.Foreground = (Brush)FindResource("Success");
            ValidationText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("win.grabber.err.save_failed", ex.Message));
        }
    }

    async Task RunGrabberAsync(SiteGrabber grabber)
    {
        try
        {
            await grabber.RunAsync(_crawlCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Crawl cancelled by user or window close.
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => StatusText.Text = Loc.T("win.grabber.status.error", ex.Message));
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                StopButton.IsEnabled = false;
                UpdateStats();
            });
        }
    }

    internal static bool ShouldCopyProjectCredentials(string? startUrl, string? fileUrl, string? login)
        => !string.IsNullOrWhiteSpace(login)
           && Uri.TryCreate(startUrl, UriKind.Absolute, out var startUri)
           && Uri.TryCreate(fileUrl, UriKind.Absolute, out var fileUri)
           && fileUri.Host.Equals(startUri.Host, StringComparison.OrdinalIgnoreCase)
           && !(startUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !fileUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    void AddFile(GrabbedFile file)
    {
        _files.Add(file);
        var group = FoundTree.Items.OfType<TreeViewItem>().FirstOrDefault(x => Equals(x.Tag, file.FoundOn));
        if (group == null)
        {
            group = new TreeViewItem
            {
                Header = new TextBlock { Text = file.FoundOn, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file.FoundOn, FlowDirection = FlowDirection.LeftToRight },
                Tag = file.FoundOn
            };
            FoundTree.Items.Add(group);
        }
        group.Items.Add(new TreeViewItem
        {
            Header = new TextBlock { Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = file.Name, FlowDirection = FlowDirection.LeftToRight },
            Tag = file
        });
        if (_project.DownloadAllAtOnce && file.Status != Loc.T("win.grabber.file_added_status"))
        {
            var item = _manager.CreateHttp(file.Url, referrer: file.FoundOn, fileName: file.Name);
            if (ShouldCopyProjectCredentials(_project.StartUrl, file.Url, _project.Login))
            {
                item.Login = _project.Login;
                item.Password = _project.Password;
            }
            item.SaveDir = SaveFolder(file);
            if (_project.LinkTextAsDescription)
            {
                item.Description = file.LinkText;
            }
            _manager.Add(item, startNow: false, QueueDef.MainId);
            file.Status = Loc.T("win.grabber.file_added_status");
            if (_grabber != null)
            {
                _grabber.Stats.IncDownloaded();
            }
            _manager.StartQueue(QueueDef.MainId);
        }
        UpdateStats();
    }

    void UpdateStats()
    {
        if (_grabber == null)
        {
            return;
        }
        var s = _grabber.Stats;
        PagesText.Text = s.PagesProcessed.ToString();
        ExploredText.Text = s.Explored.ToString();
        MatchedText.Text = s.Matched.ToString();
        DownloadedText.Text = s.Downloaded.ToString();
        StatusText.Text = s.Status;
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        _crawlCts.Cancel();
        StopButton.IsEnabled = false;
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _files)
        {
            f.Checked = true;
        }
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _files)
        {
            f.Checked = false;
        }
    }

    void Tree_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: GrabbedFile file })
        {
            FilesGrid.SelectedItem = file;
        }
    }

    void Download_Click(object sender, RoutedEventArgs e) => AddSelected(true);

    void Queue_Click(object sender, RoutedEventArgs e) => AddSelected(false);

    void AddSelected(bool start)
    {
        var addedStatus = Loc.T("win.grabber.file_added_status");
        var selected = _files.Where(f => f.Checked && f.Status != addedStatus).ToList();
        if (selected.Count == 0)
        {
            return;
        }
        var pick = App.Instance.PickQueue(this, selected.Count);
        if (pick == null)
        {
            return;
        }
        if (_manager.Settings.FindQueue(pick.Value.Queue) is { IsMain: false } targetQueue)
        {
            targetQueue.MaxSimultaneous = Math.Clamp(_project.DownloadAtOnce, 1, 10);
        }
        foreach (var file in selected)
        {
            var item = _manager.CreateHttp(file.Url, referrer: file.FoundOn, fileName: file.Name);
            if (ShouldCopyProjectCredentials(_project.StartUrl, file.Url, _project.Login))
            {
                item.Login = _project.Login;
                item.Password = _project.Password;
            }
            item.SaveDir = SaveFolder(file);
            if (_project.LinkTextAsDescription)
            {
                item.Description = file.LinkText;
            }
            _manager.Add(item, startNow: false, pick.Value.Queue);
            file.Status = addedStatus;
            _grabber!.Stats.IncDownloaded();
        }
        if (start || pick.Value.Start || _project.DownloadAllAtOnce)
        {
            _manager.StartQueue(pick.Value.Queue);
        }
        UpdateStats();
    }

    string SaveFolder(GrabbedFile file)
    {
        string root = _project.SaveMode == "folder"
            ? (AddDownloadWindow.IsAllowedFolder(_project.SaveFolder, _manager.Settings) ? _project.SaveFolder : _manager.Settings.FolderFor("general"))
            : _project.SaveMode == "onecategory"
                ? _manager.Settings.FolderFor(_project.SaveCategory)
                : _manager.Settings.FolderFor(Categories.Detect(file.Name, file.Url));
        if (!_project.KeepRelativeFolders || !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri))
        {
            return root;
        }
        var parts = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .SkipLast(1)
            .Select(FileNames.Sanitize)
            .Where(x => x.Length > 0 && x is not "." and not "..")
            .Take(6);
        return parts.Aggregate(root, Path.Combine);
    }

    void ShowError(string text)
    {
        ValidationText.Text = text;
        ValidationText.Visibility = Visibility.Visible;
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
