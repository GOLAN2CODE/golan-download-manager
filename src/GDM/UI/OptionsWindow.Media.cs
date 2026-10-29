using System.Windows;
using System.Windows.Controls;
using Golan.Gdm.Core;

namespace Golan.Gdm.UI;

public partial class OptionsWindow
{
    StackPanel PanelPage()
    {
        var p = Page(Loc.T("win.options.panel.title"), Loc.T("win.options.panel.note"));
        p.Children.Add(Card(
            Check(Loc.T("win.options.panel.enable"), () => _working.MediaPanelEnabled, v => _working.MediaPanelEnabled = v),
            Label(Loc.T("win.options.panel.min_size_kb")),
            Number(_working.MediaPanelMinSizeKB, v => _working.MediaPanelMinSizeKB = v),
            Label(Loc.T("win.options.panel.exceptions")),
            Text(_working.MediaPanelExceptions, v => _working.MediaPanelExceptions = v, true)));
        return p;
    }

    StackPanel VideoPage()
    {
        var p = Page(Loc.T("win.options.video.title"), Loc.T("win.options.video.note"));
        var status = new TextBlock
        {
            Text = MediaTools.Ready ? Loc.T("win.options.video.ready") : Loc.T("win.options.video.not_ready"),
            FontWeight = FontWeights.SemiBold
        };
        var progress = new ProgressBar
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var detail = new TextBlock
        {
            Foreground = Brush("Muted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var download = new Button { Content = Loc.T("win.options.video.download_now") };
        var update = new Button
        {
            Content = Loc.T("win.options.video.update"),
            Margin = new Thickness(8, 0, 0, 0)
        };
        download.Visibility = MediaTools.Ready ? Visibility.Collapsed : Visibility.Visible;
        update.Visibility = MediaTools.Ready ? Visibility.Visible : Visibility.Collapsed;

        async Task RunTools(bool updateOnly)
        {
            download.IsEnabled = false;
            update.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            try
            {
                if (updateOnly)
                {
                    detail.Text = await MediaTools.UpdateYtDlpAsync(CancellationToken.None);
                }
                else
                {
                    var prog = new Progress<(string Text, double Percent)>(x =>
                    {
                        detail.Text = x.Text;
                        progress.IsIndeterminate = x.Percent < 0;
                        if (x.Percent >= 0)
                        {
                            progress.Value = x.Percent;
                        }
                    });
                    await MediaTools.EnsureAsync(prog, CancellationToken.None);
                }
                status.Text = Loc.T("win.options.video.ready");
                download.Visibility = Visibility.Collapsed;
                update.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                detail.Text = Loc.T("win.options.video.failed", ex.Message);
                detail.Foreground = Brush("Danger");
                Log.Write("options media tools failed: " + ex.Message);
            }
            finally
            {
                download.IsEnabled = true;
                update.IsEnabled = true;
            }
        }

        download.Click += async (_, _) => await RunTools(false);
        update.Click += async (_, _) => await RunTools(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(download);
        buttons.Children.Add(update);
        p.Children.Add(Card(status, buttons, progress, detail));
        var qualityChoices = new[]
        {
            ("best", Loc.T("win.options.video.q.best")),
            ("2160", Loc.T("win.options.video.q.2160")),
            ("1440", Loc.T("win.options.video.q.1440")),
            ("1080", Loc.T("win.options.video.q.1080")),
            ("720", Loc.T("win.options.video.q.720")),
            ("480", Loc.T("win.options.video.q.480")),
            ("360", Loc.T("win.options.video.q.360")),
            ("mp3", Loc.T("win.options.video.q.mp3")),
            ("m4a", Loc.T("win.options.video.q.m4a"))
        };
        p.Children.Add(Card(
            Label(Loc.T("win.options.video.default_quality")),
            Combo(qualityChoices, _working.DefaultVideoQuality, v => _working.DefaultVideoQuality = v),
            Check(Loc.T("video.smart.option"), () => _working.VideoSmartMode, v => _working.VideoSmartMode = v)));

        var bitrateChoices = new[]
        {
            ("128", "128 kbps"),
            ("192", "192 kbps"),
            ("320", "320 kbps")
        };
        p.Children.Add(Card(
            Label(Loc.T($"win.options.video.defaults_card")),
            Label(Loc.T($"win.options.video.sub_langs")),
            Text(string.Join(", ", _working.DefaultSubtitleLangs), v => _working.DefaultSubtitleLangs = v.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), true),
            Check(Loc.T($"win.options.video.auto_subs"), () => _working.DefaultIncludeAutoSubs, v => _working.DefaultIncludeAutoSubs = v),
            Check(Loc.T($"win.options.video.embed_subs"), () => _working.DefaultEmbedSubs, v => _working.DefaultEmbedSubs = v),
            Check(Loc.T($"win.options.video.write_subs"), () => _working.DefaultWriteSubs, v => _working.DefaultWriteSubs = v),
            Check(Loc.T($"win.options.video.convert_srt"), () => _working.DefaultConvertSubsToSrt, v => _working.DefaultConvertSubsToSrt = v),
            Check(Loc.T($"win.options.video.write_thumb"), () => _working.DefaultWriteThumbnail, v => _working.DefaultWriteThumbnail = v),
            Check(Loc.T($"win.options.video.embed_thumb"), () => _working.DefaultEmbedThumbnail, v => _working.DefaultEmbedThumbnail = v),
            Check(Loc.T($"win.options.video.embed_chapters"), () => _working.DefaultEmbedChapters, v => _working.DefaultEmbedChapters = v),
            Check(Loc.T($"win.options.video.split_chapters"), () => _working.DefaultSplitChapters, v => _working.DefaultSplitChapters = v),
            Label(Loc.T($"win.options.video.mp3_bitrate")),
            Combo(bitrateChoices, _working.DefaultMp3Bitrate.ToString(), v =>
            {
                if (int.TryParse(v, out var br))
                    _working.DefaultMp3Bitrate = br;
            }),
            Check(Loc.T($"win.options.video.embed_metadata"), () => _working.DefaultEmbedMetadata, v => _working.DefaultEmbedMetadata = v)));

        _ensureVideoVersionLoaded = () =>
        {
            if (!MediaTools.Ready) return;
            _ = MediaTools.VersionAsync().ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result != null)
                    Dispatcher.BeginInvoke(() => status.Text += Loc.T("win.options.video.version_suffix", t.Result));
            });
        };
        return p;
    }
}
