using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Resolvers;

public static class Tests_V2
{
    static int _passed;
    static int _failed;
    static readonly List<string> _failures = [];

    static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}{(detail != null ? $" ({detail})" : "")}");
        }
        else
        {
            _failed++;
            _failures.Add(name + (detail != null ? $" ({detail})" : ""));
            Console.WriteLine($"  [FAIL] {name}{(detail != null ? $" ({detail})" : "")}");
        }
    }

    public static async Task<int> RunAsync(string baseUrl, string served, string outDir, int port)
    {
        _passed = 0;
        _failed = 0;
        _failures.Clear();

        Console.WriteLine("=== Package V2 Verification Suite (F7 Video Extras & F12 Host Resolvers) ===");
        var workRoot = Path.Combine(outDir, "_v2_tmp_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(workRoot);

        try
        {
            var repoRoot = FindRepoRoot();
            var samplesDir = Path.Combine(repoRoot, "GDM", "tests", "engine", "samples");

            Test_F7_MediaOptionsAndArgs();
            Test_F7_JsonParsing(samplesDir);
            Test_F7_SettingsAndVideoWindow(repoRoot, workRoot);
            await Test_F12_ResolversAndPatternsAsync(samplesDir);
            await Test_F12_MegaCryptoAndDownloaderAsync(workRoot);
            Test_V2_LocalizationParity(repoRoot);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] Tests_V2 unhandled exception: {ex}");
            _failed++;
        }
        finally
        {
            try { if (Directory.Exists(workRoot)) Directory.Delete(workRoot, true); } catch { }
        }

        Console.WriteLine($"\nTests_V2 Result: {_passed} passed, {_failed} failed");
        if (_failures.Count > 0)
        {
            Console.WriteLine("Tests_V2 Failures:");
            foreach (var f in _failures) Console.WriteLine($"  - {f}");
        }
        Console.WriteLine();
        return _failed;
    }

    // -------------------------------------------------------------------------
    // Part A: F7 yt-dlp extras — MediaOptions & MediaArgs.Build
    // -------------------------------------------------------------------------
    static void Test_F7_MediaOptionsAndArgs()
    {
        Console.WriteLine("V2-F7. MediaOptions & MediaArgs.Build");

        // V2-F7-01: MediaOptions defaults
        var def = new MediaOptions();
        Check("V2-F7-01: MediaOptions record defaults",
            def.Quality == "best"
            && def.SubtitleLangs.Count == 0
            && !def.IncludeAutoSubs
            && !def.EmbedSubs
            && !def.WriteSubs
            && def.ConvertSubsToSrt
            && def.PlaylistItems == null
            && def.PlaylistFrom == null
            && def.PlaylistTo == null
            && !def.WriteThumbnail
            && !def.EmbedThumbnail
            && !def.EmbedChapters
            && !def.SplitChapters
            && def.Mp3BitrateKbps == 192
            && !def.EmbedMetadata);

        // V2-F7-02: Quality = best
        var argsBest = MediaArgs.Build(new MediaOptions { Quality = "best" });
        Check("V2-F7-02: MediaArgs.Build quality 'best'",
            ContainsSequence(argsBest, "-f", "bv*+ba/b", "-S", "res,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4")
            && argsBest.Contains("--no-playlist"));

        // V2-F7-03: Quality = height (720)
        var args720 = MediaArgs.Build(new MediaOptions { Quality = "720" });
        Check("V2-F7-03: MediaArgs.Build quality '720'",
            ContainsSequence(args720, "-f", "bv*[height<=720]+ba/b[height<=720]/b", "-S", "res,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"));

        // V2-F7-04: Quality = mp3 with bitrates 128, 192, 320
        var argsMp3_128 = MediaArgs.Build(new MediaOptions { Quality = "mp3", Mp3BitrateKbps = 128 });
        var argsMp3_192 = MediaArgs.Build(new MediaOptions { Quality = "mp3", Mp3BitrateKbps = 192 });
        var argsMp3_320 = MediaArgs.Build(new MediaOptions { Quality = "mp3", Mp3BitrateKbps = 320 });
        Check("V2-F7-04: MediaArgs.Build mp3 bitrate choices (128/192/320)",
            ContainsSequence(argsMp3_128, "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "128K")
            && ContainsSequence(argsMp3_192, "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "192K")
            && ContainsSequence(argsMp3_320, "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "320K"));

        // V2-F7-05: Invalid mp3 bitrates rejected
        bool invalidBitratesRejected = true;
        foreach (var badRate in new[] { 64, 96, 256, 0, -1, 999 })
        {
            bool threw = false;
            try { MediaArgs.Build(new MediaOptions { Quality = "mp3", Mp3BitrateKbps = badRate }); }
            catch (ArgumentException) { threw = true; }
            if (!threw) invalidBitratesRejected = false;
        }
        Check("V2-F7-05: MediaArgs.Build rejects invalid mp3 bitrates", invalidBitratesRejected);

        // V2-F7-06: Subtitles separate + auto captions + convert to srt
        var argsSubsSep = MediaArgs.Build(new MediaOptions
        {
            SubtitleLangs = ["ar", "en-US"],
            WriteSubs = true,
            IncludeAutoSubs = true,
            EmbedSubs = false,
            ConvertSubsToSrt = true,
        });
        Check("V2-F7-06: MediaArgs.Build subtitles separate + auto + convert to srt",
            argsSubsSep.Contains("--write-subs")
            && argsSubsSep.Contains("--write-auto-subs")
            && !argsSubsSep.Contains("--embed-subs")
            && ContainsSequence(argsSubsSep, "--sub-langs", "ar,en-US")
            && ContainsSequence(argsSubsSep, "--convert-subs", "srt"));

        // V2-F7-07: Subtitles embed only, no auto, no srt
        var argsSubsEmbed = MediaArgs.Build(new MediaOptions
        {
            SubtitleLangs = ["ar"],
            WriteSubs = false,
            IncludeAutoSubs = false,
            EmbedSubs = true,
            ConvertSubsToSrt = false,
        });
        Check("V2-F7-07: MediaArgs.Build subtitles embed only without auto or srt conversion",
            argsSubsEmbed.Contains("--embed-subs")
            && !argsSubsEmbed.Contains("--write-auto-subs")
            && !argsSubsEmbed.Contains("--convert-subs")
            && ContainsSequence(argsSubsEmbed, "--sub-langs", "ar"));

        // V2-F7-08: Subtitle language code validation (^[a-zA-Z]{2,3}(-[a-zA-Z0-9]+)*$, max 10)
        var validLangs = new[] { "ar", "en", "zh-Hans", "pt-BR", "es-419", "fra", "deu-AT" };
        var argsValidLangs = MediaArgs.Build(new MediaOptions { SubtitleLangs = validLangs, WriteSubs = true });
        bool validLangsOk = validLangs.All(MediaArgs.IsValidLangCode)
                            && ContainsSequence(argsValidLangs, "--sub-langs", string.Join(",", validLangs));

        var badLangs = new[] { "", "a", "abcd", "en_US", "ar;--exec=calc", "--exec", "en-", "-en", "en--US", "ar,en" };
        bool badLangsRejected = badLangs.All(l => !MediaArgs.IsValidLangCode(l));
        foreach (var bl in badLangs.Where(s => s.Length > 0))
        {
            bool threw = false;
            try { MediaArgs.Build(new MediaOptions { SubtitleLangs = [bl], WriteSubs = true }); }
            catch (ArgumentException) { threw = true; }
            if (!threw) badLangsRejected = false;
        }

        bool elevenLangsRejected = false;
        try
        {
            MediaArgs.Build(new MediaOptions
            {
                SubtitleLangs = ["ar", "en", "fr", "de", "es", "it", "pt", "ru", "ja", "ko", "zh"],
                WriteSubs = true
            });
        }
        catch (ArgumentException) { elevenLangsRejected = true; }
        Check("V2-F7-08: MediaArgs validates language codes (^[a-zA-Z]{2,3}(-[a-zA-Z0-9]+)*$) and caps at max 10",
            validLangsOk && badLangsRejected && elevenLangsRejected,
            $"validOk={validLangsOk}, badRejected={badLangsRejected}, elevenRejected={elevenLangsRejected}");

        // V2-F7-09: Playlist items string and From/To range conversion
        var argsPlStr = MediaArgs.Build(new MediaOptions { PlaylistItems = "3-7,10" });
        var argsPlRange = MediaArgs.Build(new MediaOptions { PlaylistFrom = 3, PlaylistTo = 7 });
        var argsPlSingle = MediaArgs.Build(new MediaOptions { PlaylistFrom = 5, PlaylistTo = 5 });
        Check("V2-F7-09: MediaArgs.Build playlist items string and From/To conversion",
            ContainsSequence(argsPlStr, "--playlist-items", "3-7,10") && !argsPlStr.Contains("--no-playlist")
            && ContainsSequence(argsPlRange, "--playlist-items", "3-7") && !argsPlRange.Contains("--no-playlist")
            && ContainsSequence(argsPlSingle, "--playlist-items", "5"));

        // V2-F7-10: Invalid playlist items rejected
        var badPlaylists = new[] { "1-3;rm", "-o evil", "0", "0-5", "3-", "-5", "1,,2", "abc", "5-2", "1-2," };
        bool badPlaylistsRejected = true;
        foreach (var bp in badPlaylists)
        {
            bool threw = false;
            try { MediaArgs.Build(new MediaOptions { PlaylistItems = bp }); }
            catch (ArgumentException) { threw = true; }
            if (!threw) badPlaylistsRejected = false;
        }
        bool invertedFromToRejected = false;
        try { MediaArgs.Build(new MediaOptions { PlaylistFrom = 8, PlaylistTo = 3 }); }
        catch (ArgumentException) { invertedFromToRejected = true; }
        Check("V2-F7-10: MediaArgs.Build rejects invalid playlist items and inverted From/To ranges",
            badPlaylistsRejected && invertedFromToRejected);

        // V2-F7-11: Thumbnail options (write, embed, convert to jpg)
        var argsThumbWrite = MediaArgs.Build(new MediaOptions { WriteThumbnail = true, EmbedThumbnail = false });
        var argsThumbEmbed = MediaArgs.Build(new MediaOptions { WriteThumbnail = false, EmbedThumbnail = true });
        var argsThumbBoth = MediaArgs.Build(new MediaOptions { WriteThumbnail = true, EmbedThumbnail = true });
        Check("V2-F7-11: MediaArgs.Build thumbnail save/embed with --convert-thumbnails jpg",
            argsThumbWrite.Contains("--write-thumbnail") && !argsThumbWrite.Contains("--embed-thumbnail") && ContainsSequence(argsThumbWrite, "--convert-thumbnails", "jpg")
            && !argsThumbEmbed.Contains("--write-thumbnail") && argsThumbEmbed.Contains("--embed-thumbnail") && ContainsSequence(argsThumbEmbed, "--convert-thumbnails", "jpg")
            && argsThumbBoth.Contains("--write-thumbnail") && argsThumbBoth.Contains("--embed-thumbnail") && ContainsSequence(argsThumbBoth, "--convert-thumbnails", "jpg"));

        // V2-F7-12: Chapters & metadata
        var argsChapMeta = MediaArgs.Build(new MediaOptions { EmbedChapters = true, SplitChapters = true, EmbedMetadata = true });
        Check("V2-F7-12: MediaArgs.Build chapters (--embed-chapters, --split-chapters) and metadata (--embed-metadata)",
            argsChapMeta.Contains("--embed-chapters")
            && argsChapMeta.Contains("--split-chapters")
            && argsChapMeta.Contains("--embed-metadata")
            && !argsBest.Contains("--embed-chapters")
            && !argsBest.Contains("--split-chapters")
            && !argsBest.Contains("--embed-metadata"));

        // V2-F7-13: All-options matrix & token purity
        var argsAll = MediaArgs.Build(new MediaOptions
        {
            Quality = "1080",
            SubtitleLangs = ["ar", "en"],
            IncludeAutoSubs = true,
            WriteSubs = true,
            EmbedSubs = true,
            ConvertSubsToSrt = true,
            PlaylistItems = "1-3,5",
            WriteThumbnail = true,
            EmbedThumbnail = true,
            EmbedChapters = true,
            SplitChapters = true,
            EmbedMetadata = true,
        }, outputTemplate: @"C:\Out\%(title).150B.%(ext)s", ffmpegDir: @"C:\Tools", rateLimitBytesPerSec: 512 * 1024);

        bool discreteTokens = argsAll.Where(a => a.StartsWith('-')).All(a => !a.Contains(' '));
        Check("V2-F7-13: MediaArgs.Build full matrix produces discrete argument tokens (never concatenated shell flags)",
            discreteTokens
            && ContainsSequence(argsAll, "--ffmpeg-location", @"C:\Tools")
            && ContainsSequence(argsAll, "-o", @"C:\Out\%(title).150B.%(ext)s")
            && ContainsSequence(argsAll, "--limit-rate", "512K"));
    }

    // -------------------------------------------------------------------------
    // Part A.2: F7 JSON Parsing (-J output)
    // -------------------------------------------------------------------------
    static void Test_F7_JsonParsing(string samplesDir)
    {
        Console.WriteLine("V2-F7. JSON Parsing (-J Subtitles, Chapters, ThumbnailUrl)");

        // V2-F7-14: Single video sample
        var videoJson = File.ReadAllText(Path.Combine(samplesDir, "ytdlp_video.json"), Encoding.UTF8);
        var info = MediaDownloader.ParseInfo(videoJson, "https://www.youtube.com/watch?v=abc123xyz");
        Check("V2-F7-14a: ParseInfo extracts Title, ThumbnailUrl, Duration, and Qualities",
            info.Title == "Sample Lecture"
            && info.ThumbnailUrl == "https://i.ytimg.com/vi/abc/maxresdefault.jpg"
            && info.Thumbnail == "https://i.ytimg.com/vi/abc/maxresdefault.jpg"
            && Math.Abs(info.Duration - 615.0) < 0.01
            && info.Qualities.Any(q => q.Key == "best")
            && info.Qualities.Any(q => q.Key == "720")
            && info.Qualities.Any(q => q.Key == "mp3" && q.AudioOnly));

        bool hasManualAr = info.Subtitles.Any(s => s.Lang == "ar" && s.Name == "العربية" && !s.IsAuto);
        bool hasManualEn = info.Subtitles.Any(s => s.Lang == "en" && s.Name == "English" && !s.IsAuto);
        bool hasAutoFr = info.Subtitles.Any(s => s.Lang == "fr" && s.IsAuto);
        Check("V2-F7-14b: ParseInfo extracts Subtitles (manual + automatic_captions with IsAuto flag)",
            hasManualAr && hasManualEn && hasAutoFr,
            $"count={info.Subtitles.Count}, tracks=[{string.Join(", ", info.Subtitles.Select(s => $"{s.Lang}:{s.Name}:auto={s.IsAuto}"))}]");

        Check("V2-F7-14c: ParseInfo extracts Chapters with Title, StartTime, EndTime",
            info.Chapters.Count == 3
            && info.Chapters[0].Title == "Introduction" && Math.Abs(info.Chapters[0].StartTime - 0.0) < 0.01 && Math.Abs(info.Chapters[0].EndTime - 120.5) < 0.01
            && info.Chapters[1].Title == "Core Concepts" && Math.Abs(info.Chapters[1].StartTime - 120.5) < 0.01
            && info.Chapters[2].Title == "Summary" && Math.Abs(info.Chapters[2].EndTime - 615.0) < 0.01);

        // V2-F7-15: Playlist sample
        var plJson = File.ReadAllText(Path.Combine(samplesDir, "ytdlp_playlist.json"), Encoding.UTF8);
        var plInfo = MediaDownloader.ParseInfo(plJson, "https://www.youtube.com/playlist?list=PL123456");
        Check("V2-F7-15: ParseInfo parses playlist entries, expands Youtube ID, sets ThumbnailUrl, skips non-HTTP",
            plInfo.IsPlaylist
            && plInfo.Entries.Count == 3
            && plInfo.Entries[0].Url == "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
            && plInfo.ThumbnailUrl == "https://i.ytimg.com/vi/thumb2/maxresdefault.jpg");
    }

    // -------------------------------------------------------------------------
    // Part A.3: Settings.V2.cs & VideoWindow controls
    // -------------------------------------------------------------------------
    static void Test_F7_SettingsAndVideoWindow(string repoRoot, string workRoot)
    {
        Console.WriteLine("V2-F7. Settings.V2 persistence & VideoWindow controls");

        var s = new AppSettings
        {
            DefaultSubtitleLangs = ["ar", "en"],
            DefaultIncludeAutoSubs = true,
            DefaultEmbedSubs = true,
            DefaultWriteSubs = true,
            DefaultConvertSubsToSrt = true,
            DefaultWriteThumbnail = true,
            DefaultEmbedThumbnail = true,
            DefaultEmbedChapters = true,
            DefaultSplitChapters = false,
            DefaultMp3Bitrate = 320,
            DefaultEmbedMetadata = true,
        };
        var json = JsonSerializer.Serialize(s, Json.Options);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, Json.Options)!;
        restored.Normalize();
        Check("V2-F7-16a: Settings.V2 round-trips video extra defaults via JSON and Normalize()",
            restored.DefaultSubtitleLangs.SequenceEqual(["ar", "en"])
            && restored.DefaultIncludeAutoSubs
            && restored.DefaultEmbedSubs
            && restored.DefaultWriteSubs
            && restored.DefaultConvertSubsToSrt
            && restored.DefaultWriteThumbnail
            && restored.DefaultEmbedThumbnail
            && restored.DefaultEmbedChapters
            && !restored.DefaultSplitChapters
            && restored.DefaultMp3Bitrate == 320
            && restored.DefaultEmbedMetadata);

        var xamlPath = Path.Combine(repoRoot, "GDM", "src", "GDM", "UI", "VideoWindow.xaml");
        var csPath = Path.Combine(repoRoot, "GDM", "src", "GDM", "UI", "VideoWindow.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var cs = File.ReadAllText(csPath);
        Check("V2-F7-16b: VideoWindow.xaml(.cs) includes subtitle checklist, playlist range fields, thumbnail/chapters/metadata checkboxes, and mp3 bitrate selector",
            xaml.Contains("Subtitle") && xaml.Contains("PlaylistFrom") && xaml.Contains("PlaylistTo")
            && xaml.Contains("Thumbnail") && xaml.Contains("Chapters") && xaml.Contains("Metadata")
            && xaml.Contains("Mp3Bitrate")
            && cs.Contains("MediaOptions"));
    }

    // -------------------------------------------------------------------------
    // Part B: F12 Host Resolvers & Pattern Matching
    // -------------------------------------------------------------------------
    static async Task Test_F12_ResolversAndPatternsAsync(string samplesDir)
    {
        Console.WriteLine("V2-F12. Host Resolvers, Pattern Matching, Recorded HTML, Security");

        // V2-F12-01 & Pattern table across all 6 resolvers
        var gdrive = new GoogleDriveResolver();
        var dropbox = new DropboxResolver();
        var onedrive = new OneDriveResolver();
        var mediafire = new MediaFireResolver();
        var github = new GitHubResolver();
        var mega = new MegaResolver();

        Check("V2-F12-01: HostResolvers.All contains all 6 resolvers (Drive, Dropbox, OneDrive, MediaFire, GitHub, MEGA)",
            HostResolvers.All.Count >= 6
            && gdrive.CanHandle(new Uri("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz_01234/view"))
            && dropbox.CanHandle(new Uri("https://www.dropbox.com/s/abc/file.zip?dl=0"))
            && onedrive.CanHandle(new Uri("https://1drv.ms/u/s!Am123"))
            && mediafire.CanHandle(new Uri("https://www.mediafire.com/file/xyz/archive.rar/file"))
            && github.CanHandle(new Uri("https://github.com/owner/repo/blob/main/file.zip"))
            && mega.CanHandle(new Uri("https://mega.nz/file/AbCdEfGh#0123456789abcdef0123456789abcdef0123456789a")));

        // V2-F12-02: Timeout (20s default, overridden to 60ms in test) and error fallback return null
        using (var slowInvoker = new HttpMessageInvoker(new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(2000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })))
        {
            var timedOut = await HostResolvers.TryResolveAsync(
                new Uri("https://www.mediafire.com/file/abc/test.zip/file"),
                slowInvoker,
                CancellationToken.None,
                timeout: TimeSpan.FromMilliseconds(60));
            Check("V2-F12-02a: HostResolvers.TryResolveAsync returns null on timeout", timedOut == null);
        }

        using (var errInvoker = new HttpMessageInvoker(new FakeHttpHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)))))
        {
            var failed = await HostResolvers.TryResolveAsync(
                new Uri("https://www.mediafire.com/file/abc/test.zip/file"),
                errInvoker,
                CancellationToken.None);
            Check("V2-F12-02b: HostResolvers.TryResolveAsync returns null on HTTP error", failed == null);
        }

        // V2-F12-03: Cookie isolation & HTTPS-only redirects
        bool sawCookieHeader = false;
        using (var cookieCheckInvoker = new HttpMessageInvoker(new FakeHttpHandler((req, _) =>
        {
            if (req.Headers.Contains("Cookie")) sawCookieHeader = true;
            var html = File.ReadAllText(Path.Combine(samplesDir, "mediafire_direct.html"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            });
        })))
        {
            var res = await HostResolvers.TryResolveAsync(
                new Uri("https://www.mediafire.com/file/abc/project_backup.rar/file"),
                cookieCheckInvoker,
                CancellationToken.None);
            Check("V2-F12-03: Resolvers never send user cookies", !sawCookieHeader && res != null);
        }

        // V2-F12-04: Redirect following only to HTTPS (rejects HTTP redirect downgrade)
        using (var downgradeInvoker = new HttpMessageInvoker(new FakeHttpHandler((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Found);
            resp.Headers.Location = new Uri("http://download1500.mediafire.com/insecure/file.rar");
            return Task.FromResult(resp);
        })))
        {
            bool downgradeBlocked = false;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.mediafire.com/file/abc/test.rar");
                await HostResolvers.SendFollowHttpsAsync(downgradeInvoker, req, CancellationToken.None);
            }
            catch (DownloadException) { downgradeBlocked = true; }
            Check("V2-F12-04: HostResolvers.SendFollowHttpsAsync blocks HTTPS -> HTTP redirect downgrade", downgradeBlocked);
        }

        // V2-F12-05: Google Drive ID extraction (`/file/d/<id>`, `open?id=`, `uc?id=`) & folder rejection
        bool gd1 = GoogleDriveResolver.TryExtractFileId(new Uri("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz_01234/view?usp=sharing"), out var id1, out var f1);
        bool gd2 = GoogleDriveResolver.TryExtractFileId(new Uri("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOpQrStUvWxYz_01234"), out var id2, out var f2);
        bool gd3 = GoogleDriveResolver.TryExtractFileId(new Uri("https://drive.google.com/uc?id=1AbCdEfGhIjKlMnOpQrStUvWxYz_01234&export=download"), out var id3, out var f3);
        bool gdFolder = GoogleDriveResolver.TryExtractFileId(new Uri("https://drive.google.com/drive/folders/1FolderId999"), out _, out var isFolder);
        Check("V2-F12-05: GoogleDriveResolver extracts IDs from /file/d/, open?id=, uc?id= and detects folders",
            gd1 && id1 == "1AbCdEfGhIjKlMnOpQrStUvWxYz_01234" && !f1
            && gd2 && id2 == "1AbCdEfGhIjKlMnOpQrStUvWxYz_01234" && !f2
            && gd3 && id3 == "1AbCdEfGhIjKlMnOpQrStUvWxYz_01234" && !f3
            && !gdFolder && isFolder);

        bool folderThrewUnsupported = false;
        try
        {
            using var dummyInvoker = new HttpMessageInvoker(new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
            await gdrive.ResolveAsync(new Uri("https://drive.google.com/drive/folders/1FolderId999"), dummyInvoker, CancellationToken.None);
        }
        catch (DownloadException ex) when (!string.IsNullOrWhiteSpace(ex.Message))
        {
            folderThrewUnsupported = true;
        }
        Check("V2-F12-05b: GoogleDriveResolver rejects folder links with localized unsupported message", folderThrewUnsupported);

        // V2-F12-06: Google Drive direct response
        using (var gdDirectInvoker = new HttpMessageInvoker(new FakeHttpHandler((req, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            resp.Content.Headers.ContentLength = 1048576;
            resp.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "\"archive.zip\"" };
            return Task.FromResult(resp);
        })))
        {
            var gdLink = await gdrive.ResolveAsync(
                new Uri("https://drive.google.com/file/d/1AbCdEfGhIjKlMnOpQrStUvWxYz_01234/view"),
                gdDirectInvoker,
                CancellationToken.None);
            Check("V2-F12-06: GoogleDriveResolver direct download response",
                gdLink != null
                && gdLink.Direct.ToString() == "https://drive.usercontent.google.com/download?id=1AbCdEfGhIjKlMnOpQrStUvWxYz_01234&export=download"
                && gdLink.FileName == "archive.zip"
                && gdLink.Size == 1048576);
        }

        // V2-F12-07: Google Drive confirm-page HTML parsing (recorded HTML sample)
        var confirmHtml = File.ReadAllText(Path.Combine(samplesDir, "gdrive_confirm.html"), Encoding.UTF8);
        var parsedConfirmUri = GoogleDriveResolver.ParseConfirmFormHtml(
            confirmHtml,
            new Uri("https://drive.usercontent.google.com/download?id=1AbCdEfGhIjKlMnOpQrStUvWxYz_01234&export=download"));
        Check("V2-F12-07a: GoogleDriveResolver.ParseConfirmFormHtml builds URL from form action + id, export, confirm, uuid and ignores <script>",
            parsedConfirmUri != null
            && parsedConfirmUri.Host == "drive.usercontent.google.com"
            && parsedConfirmUri.Query.Contains("id=1AbCdEfGhIjKlMnOpQrStUvWxYz_01234")
            && parsedConfirmUri.Query.Contains("export=download")
            && parsedConfirmUri.Query.Contains("confirm=t")
            && parsedConfirmUri.Query.Contains("uuid=9f8e7d6c-1234-4a5b-8c9d-0e1f2a3b4c5d")
            && !parsedConfirmUri.ToString().Contains("evil.example.com"),
            parsedConfirmUri?.ToString());

        using (var gdConfirmInvoker = new HttpMessageInvoker(new FakeHttpHandler((req, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(confirmHtml, Encoding.UTF8, "text/html")
            };
            return Task.FromResult(resp);
        })))
        {
            var gdConfirmed = await gdrive.ResolveAsync(
                new Uri("https://drive.google.com/open?id=1AbCdEfGhIjKlMnOpQrStUvWxYz_01234"),
                gdConfirmInvoker,
                CancellationToken.None);
            Check("V2-F12-07b: GoogleDriveResolver.ResolveAsync handles HTML virus-scan page and extracts filename",
                gdConfirmed != null
                && gdConfirmed.Direct.Query.Contains("confirm=t")
                && gdConfirmed.Direct.Query.Contains("uuid=9f8e7d6c-1234-4a5b-8c9d-0e1f2a3b4c5d")
                && gdConfirmed.FileName == "ubuntu-24.04-desktop-amd64.iso");
        }

        // V2-F12-08: Dropbox URL rewrite (dl=1, preserve rlkey)
        var dbx1 = DropboxResolver.RewriteUrl(new Uri("https://www.dropbox.com/s/abc123xyz/report.pdf?dl=0"));
        var dbx2 = DropboxResolver.RewriteUrl(new Uri("https://www.dropbox.com/scl/fi/id123/project.zip?rlkey=sec987&dl=0"));
        var dbx3 = DropboxResolver.RewriteUrl(new Uri("https://www.dropbox.com/scl/fi/id123/noquery.zip"));
        Check("V2-F12-08: DropboxResolver sets dl=1 and preserves rlkey",
            dbx1.ToString() == "https://www.dropbox.com/s/abc123xyz/report.pdf?dl=1"
            && dbx2.Query.Contains("rlkey=sec987") && dbx2.Query.Contains("dl=1") && !dbx2.Query.Contains("dl=0")
            && dbx3.ToString() == "https://www.dropbox.com/scl/fi/id123/noquery.zip?dl=1");

        // V2-F12-09: OneDrive share -> u!<base64url> & SharePoint -> download=1
        var odShareUrl = new Uri("https://1drv.ms/u/s!Am1234567890");
        var odLiveUrl = new Uri("https://onedrive.live.com/?resid=ABC!123&ithint=file%2czip");
        var spUrl = new Uri("https://contoso-my.sharepoint.com/:u:/g/personal/user/Eabc123?e=xyz");
        var expectedToken = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(odShareUrl.ToString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var odRewritten1 = OneDriveResolver.RewriteUrl(odShareUrl);
        var odRewritten2 = OneDriveResolver.RewriteUrl(odLiveUrl);
        var spRewritten = OneDriveResolver.RewriteUrl(spUrl);
        Check("V2-F12-09: OneDriveResolver encodes 1drv.ms / onedrive.live.com to api.onedrive.com u!<base64url> and adds download=1 for SharePoint",
            odRewritten1.ToString() == $"https://api.onedrive.com/v1.0/shares/{expectedToken}/root/content"
            && odRewritten2.ToString().StartsWith("https://api.onedrive.com/v1.0/shares/u!", StringComparison.Ordinal)
            && spRewritten.Host == "contoso-my.sharepoint.com"
            && spRewritten.Query.Contains("e=xyz")
            && spRewritten.Query.Contains("download=1"));

        // V2-F12-10: MediaFire direct + scrambled HTML parsing + HTTPS *.mediafire.com enforcement
        var mfDirectHtml = File.ReadAllText(Path.Combine(samplesDir, "mediafire_direct.html"), Encoding.UTF8);
        var mfScrambledHtml = File.ReadAllText(Path.Combine(samplesDir, "mediafire_scrambled.html"), Encoding.UTF8);
        var mfDirectUri = MediaFireResolver.ParseDownloadPageHtml(mfDirectHtml);
        var mfScrambledUri = MediaFireResolver.ParseDownloadPageHtml(mfScrambledHtml);
        var mfInsecureUri = MediaFireResolver.ParseDownloadPageHtml("<a id=\"downloadButton\" href=\"http://download1500.mediafire.com/a/b.zip\">dl</a>");
        var mfEvilHostUri = MediaFireResolver.ParseDownloadPageHtml("<a id=\"downloadButton\" href=\"https://evil.example.com/mediafire.com/b.zip\">dl</a>");
        Check("V2-F12-10: MediaFireResolver parses direct href and base64 data-scrambled-url, and enforces https on *.mediafire.com",
            mfDirectUri?.ToString() == "https://download1500.mediafire.com/abcdef12345/xyz987/project_backup.rar"
            && mfScrambledUri?.ToString() == "https://download1920.mediafire.com/sectoken/key123/scrambled_release.zip"
            && mfInsecureUri == null
            && mfEvilHostUri == null);

        // V2-F12-11: GitHub blob -> raw.githubusercontent.com
        var gh1 = GitHubResolver.RewriteBlobUrl(new Uri("https://github.com/octocat/Hello-World/blob/main/README.md"));
        var gh2 = GitHubResolver.RewriteBlobUrl(new Uri("https://github.com/owner/repo/blob/v1.2.3/src/dir/file%20name.cs"));
        var ghNonBlob = GitHubResolver.RewriteBlobUrl(new Uri("https://github.com/owner/repo/issues/1"));
        Check("V2-F12-11: GitHubResolver rewrites github.com/<o>/<r>/blob/<ref>/<path> to raw.githubusercontent.com",
            gh1?.AbsoluteUri == "https://raw.githubusercontent.com/octocat/Hello-World/main/README.md"
            && gh2?.AbsoluteUri == "https://raw.githubusercontent.com/owner/repo/v1.2.3/src/dir/file%20name.cs"
            && ghNonBlob == null
            && !github.CanHandle(new Uri("https://github.com/owner/repo/issues/1")));
    }

    // -------------------------------------------------------------------------
    // Part C: F12 MEGA Cryptography, Resolver & MegaDownloader
    // -------------------------------------------------------------------------
    static async Task Test_F12_MegaCryptoAndDownloaderAsync(string workRoot)
    {
        Console.WriteLine("V2-F12. MEGA Crypto (Key Unfold, Attributes AES-CBC, AES-CTR Arbitrary Offsets) & MegaDownloader");

        // Build deterministic 32-byte raw MEGA key
        byte[] rawKey32 = new byte[32];
        for (int i = 0; i < 32; i++) rawKey32[i] = (byte)(i * 7 + 3);
        string keyB64Url = Convert.ToBase64String(rawKey32).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // V2-F12-12: MegaResolver link parsing (modern & legacy)
        bool mModern = MegaResolver.TryParseLink(new Uri($"https://mega.nz/file/AbCdEfGh#{keyB64Url}"), out var idMod, out var kMod);
        bool mLegacy = MegaResolver.TryParseLink(new Uri($"https://mega.nz/#!XyZ12345!{keyB64Url}"), out var idLeg, out var kLeg);
        bool mFolder = MegaResolver.TryParseLink(new Uri($"https://mega.nz/folder/AbCdEfGh#{keyB64Url}"), out _, out _);
        Check("V2-F12-12: MegaResolver parses modern /file/<id>#<key> and legacy #!<id>!<key>, rejects folders",
            mModern && idMod == "AbCdEfGh" && kMod.SequenceEqual(rawKey32)
            && mLegacy && idLeg == "XyZ12345" && kLeg.SequenceEqual(rawKey32)
            && !mFolder);

        // V2-F12-13: MegaResolver.UnfoldKey (key = k[0..3] xor k[4..7], nonce = k[4..5])
        var (aesKey16, nonce8) = MegaResolver.UnfoldKey(rawKey32);
        byte[] expectedAes16 = new byte[16];
        for (int i = 0; i < 16; i++) expectedAes16[i] = (byte)(rawKey32[i] ^ rawKey32[i + 16]);
        byte[] expectedNonce8 = rawKey32[16..24];
        Check("V2-F12-13: MegaResolver.UnfoldKey computes 16-byte XOR key and 8-byte nonce",
            aesKey16.SequenceEqual(expectedAes16) && nonce8.SequenceEqual(expectedNonce8));

        // V2-F12-14: Attribute decode (AES-CBC zero IV -> MEGA{"n":"..."}) + path traversal sanitization
        byte[] EncryptMegaAt(string jsonPayload)
        {
            var rawBytes = Encoding.UTF8.GetBytes("MEGA" + jsonPayload);
            int paddedLen = ((rawBytes.Length + 15) / 16) * 16;
            byte[] padded = new byte[paddedLen];
            Buffer.BlockCopy(rawBytes, 0, padded, 0, rawBytes.Length);
            using var aes = Aes.Create();
            aes.Key = aesKey16;
            aes.IV = new byte[16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor();
            return enc.TransformFinalBlock(padded, 0, padded.Length);
        }

        var encAtClean = EncryptMegaAt("{\"n\":\"lecture_notes.pdf\"}");
        var encAtTraversal = EncryptMegaAt("{\"n\":\"../../Evil/..\\\\autorun.inf\"}");
        var decNameClean = MegaResolver.DecryptAttributesFileName(encAtClean, aesKey16);
        var decNameTraversal = MegaResolver.DecryptAttributesFileName(encAtTraversal, aesKey16);
        Check("V2-F12-14: MegaResolver.DecryptAttributesFileName decrypts AES-CBC zero-IV attributes and sanitizes traversal names",
            decNameClean == "lecture_notes.pdf"
            && decNameTraversal == "autorun.inf",
            $"clean='{decNameClean}', traversal='{decNameTraversal}'");

        // V2-F12-15: MegaResolver.ResolveAsync mocked API call
        string atB64Url = Convert.ToBase64String(encAtClean).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string? postedBody = null;
        using (var megaApiInvoker = new HttpMessageInvoker(new FakeHttpHandler(async (req, ct) =>
        {
            postedBody = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
            var jsonResp = $"[{{\"g\":\"https://gfs200.userstorage.mega.co.nz/dl/obj123\",\"s\":262144,\"at\":\"{atB64Url}\"}}]";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResp, Encoding.UTF8, "application/json")
            };
        })))
        {
            var megaResolver = new MegaResolver();
            var resolved = await megaResolver.ResolveAsync(
                new Uri($"https://mega.nz/file/AbCdEfGh#{keyB64Url}"),
                megaApiInvoker,
                CancellationToken.None);
            Check("V2-F12-15: MegaResolver.ResolveAsync posts [{\"a\":\"g\",\"g\":1,\"p\":\"<id>\"}] and returns ResolvedLink with NeedsMegaDecrypt=true",
                resolved != null
                && postedBody != null && postedBody.Contains("\"a\":\"g\"") && postedBody.Contains("\"p\":\"AbCdEfGh\"")
                && resolved.Direct.ToString() == "https://gfs200.userstorage.mega.co.nz/dl/obj123"
                && resolved.FileName == "lecture_notes.pdf"
                && resolved.Size == 262144
                && resolved.NeedsMegaDecrypt
                && resolved.MegaKey != null && resolved.MegaKey.SequenceEqual(rawKey32));
        }

        // V2-F12-16: MegaCrypto.TransformCtr arbitrary offset equivalence (offset / 16 counter + offset % 16 skip)
        byte[] plainData = new byte[100 * 1024 + 37];
        new Random(42).NextBytes(plainData);
        byte[] cipherData = new byte[plainData.Length];
        MegaCrypto.TransformCtr(aesKey16, nonce8, 0, plainData, cipherData);

        bool allOffsetsMatch = true;
        foreach (int splitOffset in new[] { 0, 1, 7, 15, 16, 17, 31, 32, 1023, 4099, 65537, plainData.Length - 1 })
        {
            byte[] sliceOut = new byte[plainData.Length - splitOffset];
            MegaCrypto.TransformCtr(aesKey16, nonce8, splitOffset, cipherData.AsSpan(splitOffset), sliceOut);
            if (!sliceOut.AsSpan().SequenceEqual(plainData.AsSpan(splitOffset)))
            {
                allOffsetsMatch = false;
                break;
            }
        }
        Check("V2-F12-16: MegaCrypto.TransformCtr at arbitrary byte offsets (counter=offset/16, skip=offset%16) equals full decrypt",
            allOffsetsMatch);

        // V2-F12-17: MegaDownloader end-to-end + pause/resume at non-block-aligned offset + optional chunk-MAC verification
        byte[] filePlain = new byte[256 * 1024 + 19];
        new Random(1337).NextBytes(filePlain);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(filePlain));

        // Compute valid MEGA key with meta-MAC embedded in k[6..7] so chunk-MAC check passes
        byte[] keyWithMac = MegaCrypto.BuildKeyWithMetaMac(filePlain, aesKey16, nonce8);
        var (dlAesKey, dlNonce) = MegaResolver.UnfoldKey(keyWithMac);
        byte[] fileCipher = new byte[filePlain.Length];
        MegaCrypto.TransformCtr(dlAesKey, dlNonce, 0, filePlain, fileCipher);

        int requestCount = 0;
        long secondRequestRangeStart = -1;
        using var pauseCts = new CancellationTokenSource();

        using (var dlInvoker = new HttpMessageInvoker(new FakeHttpHandler((req, _) =>
        {
            requestCount++;
            long start = req.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            if (requestCount == 2) secondRequestRangeStart = start;

            var slice = fileCipher.AsMemory((int)start);
            var stream = new ThrottledCallbackStream(slice, bytesReadSoFar =>
            {
                // Cancel mid-stream on the first run at a non-16-byte-aligned position (~70 KB + 5 bytes)
                if (requestCount == 1 && bytesReadSoFar >= 70 * 1024 + 5)
                    pauseCts.Cancel();
            });

            var resp = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            resp.Content.Headers.ContentLength = slice.Length;
            if (start > 0)
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, fileCipher.Length - 1, fileCipher.Length);
            return Task.FromResult(resp);
        })))
        {
            var megaItem = new DownloadItem
            {
                Url = "https://gfs200.userstorage.mega.co.nz/dl/obj123",
                SaveDir = workRoot,
                FileName = "mega_resumed.bin",
                NameLocked = true,
                TotalSize = filePlain.Length,
            };

            var downloader1 = new MegaDownloader(megaItem, keyWithMac, new SpeedLimiter(), dlInvoker, verifyMac: true);
            bool pausedCleanly = false;
            try { await downloader1.RunAsync(pauseCts.Token); }
            catch (OperationCanceledException) { pausedCleanly = true; }

            long pausedBytes = megaItem.Downloaded;
            bool tempExistsAfterPause = File.Exists(megaItem.TempPath) && new FileInfo(megaItem.TempPath).Length == pausedBytes;

            // Resume with a second MegaDownloader instance
            var downloader2 = new MegaDownloader(megaItem, keyWithMac, new SpeedLimiter(), dlInvoker, verifyMac: true);
            await downloader2.RunAsync(CancellationToken.None);

            string actualSha256 = File.Exists(megaItem.FullPath)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(megaItem.FullPath)))
                : "";

            Check("V2-F12-17a: MegaDownloader pauses mid-stream, resumes from exact byte offset with AES-CTR counter/skip, and matches SHA-256",
                pausedCleanly
                && pausedBytes >= 70 * 1024
                && tempExistsAfterPause
                && secondRequestRangeStart == pausedBytes
                && actualSha256 == expectedSha256,
                $"pausedAt={pausedBytes}, rangeStart={secondRequestRangeStart}, shaMatch={actualSha256 == expectedSha256}");
        }

        // V2-F12-17b: Tampered ciphertext detected by chunk-MAC verification
        byte[] tamperedCipher = (byte[])fileCipher.Clone();
        tamperedCipher[tamperedCipher.Length / 2] ^= 0xFF;
        using (var tamperedInvoker = new HttpMessageInvoker(new FakeHttpHandler((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(tamperedCipher)
            };
            resp.Content.Headers.ContentLength = tamperedCipher.Length;
            return Task.FromResult(resp);
        })))
        {
            var badItem = new DownloadItem
            {
                Url = "https://gfs200.userstorage.mega.co.nz/dl/tampered",
                SaveDir = workRoot,
                FileName = "mega_tampered.bin",
                NameLocked = true,
                TotalSize = filePlain.Length,
            };
            bool macRejected = false;
            try
            {
                var badDownloader = new MegaDownloader(badItem, keyWithMac, new SpeedLimiter(), tamperedInvoker, verifyMac: true);
                await badDownloader.RunAsync(CancellationToken.None);
            }
            catch (DownloadException ex) when (ex.Kind == FailKind.Mismatch)
            {
                macRejected = true;
            }
            Check("V2-F12-17b: MegaDownloader chunk-MAC check rejects corrupted ciphertext and deletes bad output",
                macRejected && !File.Exists(badItem.FullPath));
        }

        // V2-F12-18: Security checks — SSRF private/loopback blocking, MegaDownloader UNC blocking, and MegaKey DPAPI + fragment stripping
        bool ssrfBlocked = !HostResolvers.IsSafePublicHttpsUri(new Uri("https://127.0.0.1:8443/internal"))
                           && !HostResolvers.IsSafePublicHttpsUri(new Uri("https://192.168.1.1/router"))
                           && !HostResolvers.IsSafePublicHttpsUri(new Uri("https://169.254.169.254/latest/meta-data"))
                           && !HostResolvers.IsSafePublicHttpsUri(new Uri("https://localhost/admin"));

        bool uncBlockedInMega = false;
        try
        {
            var uncItem = new DownloadItem
            {
                Url = "https://gfs200.userstorage.mega.co.nz/dl/obj123",
                SaveDir = @"\\192.0.2.99\unconfigured_share\folder",
                FileName = "test.bin",
            };
            var uncDl = new MegaDownloader(uncItem, keyWithMac, new SpeedLimiter());
            await uncDl.RunAsync(CancellationToken.None);
        }
        catch (DownloadException)
        {
            uncBlockedInMega = true;
        }

        var dpapiItem = new DownloadItem
        {
            Url = $"https://mega.nz/file/AbCdEfGh#{keyB64Url}",
            NeedsMegaDecrypt = true,
            MegaKey = rawKey32,
        };
        var itemJson = JsonSerializer.Serialize(dpapiItem, Json.Options);
        var restoredItem = JsonSerializer.Deserialize<DownloadItem>(itemJson, Json.Options)!;
        var parsedIndices = MediaArgs.ParsePlaylistIndices("3-5,8");
        Check("V2-F12-18: SSRF private IP guard, MegaDownloader UNC SaveDir guard, MegaKey DPAPI round-trip without cleartext key in JSON, and ParsePlaylistIndices",
            ssrfBlocked
            && uncBlockedInMega
            && !itemJson.Contains(keyB64Url, StringComparison.Ordinal)
            && !dpapiItem.Url.Contains('#')
            && restoredItem.MegaKey != null && restoredItem.MegaKey.SequenceEqual(rawKey32)
            && parsedIndices.SetEquals([3, 4, 5, 8]));
    }

    // -------------------------------------------------------------------------
    // Part D: Localization Parity for v2.ar.json and v2.en.json
    // -------------------------------------------------------------------------
    static void Test_V2_LocalizationParity(string repoRoot)
    {
        Console.WriteLine("V2-i18n. v2.ar.json and v2.en.json key & placeholder parity");
        var i18nDir = Path.Combine(repoRoot, "GDM", "src", "GDM", "i18n");
        var arPath = Path.Combine(i18nDir, "media.ar.json");
        var enPath = Path.Combine(i18nDir, "media.en.json");

        bool filesExist = File.Exists(arPath) && File.Exists(enPath);
        if (!filesExist)
        {
            Check("V2-i18n: v2.ar.json and v2.en.json exist", false);
            return;
        }

        var ar = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(arPath)) ?? [];
        var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath)) ?? [];

        var missingInEn = ar.Keys.Where(k => !en.ContainsKey(k)).ToList();
        var missingInAr = en.Keys.Where(k => !ar.ContainsKey(k)).ToList();

        var phRegex = new Regex(@"\{\d+\}", RegexOptions.Compiled);
        var phMismatches = new List<string>();
        foreach (var k in ar.Keys.Where(en.ContainsKey))
        {
            var phAr = string.Join(",", phRegex.Matches(ar[k]).Select(m => m.Value).Distinct().OrderBy(x => x));
            var phEn = string.Join(",", phRegex.Matches(en[k]).Select(m => m.Value).Distinct().OrderBy(x => x));
            if (phAr != phEn) phMismatches.Add(k);
        }

        // Scan V2 XAML and C# files for {v2:T ...} and V2Loc.Get("...") and ensure all exist in media.ar.json & media.en.json
        var v2Files = new List<string>
        {
            Path.Combine(repoRoot, "GDM", "src", "GDM", "UI", "VideoWindow.xaml"),
            Path.Combine(repoRoot, "GDM", "src", "GDM", "UI", "VideoWindow.xaml.cs"),
            Path.Combine(repoRoot, "GDM", "src", "GDM", "Core", "MediaDownloader.cs"),
            Path.Combine(repoRoot, "GDM", "src", "GDM", "Core", "Settings.Media.cs"),
        };
        var resolversDir = Path.Combine(repoRoot, "GDM", "src", "GDM", "Core", "Resolvers");
        if (Directory.Exists(resolversDir))
            v2Files.AddRange(Directory.GetFiles(resolversDir, "*.cs"));

        var xamlV2Rx = new Regex(@"\{v2:T\s+([^\}\s,]+)", RegexOptions.Compiled);
        var csV2Rx = new Regex(@"V2Loc\.Get\(\s*""([^""]+)""", RegexOptions.Compiled);
        var referencedV2Keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in v2Files.Where(File.Exists))
        {
            var content = File.ReadAllText(file);
            foreach (Match m in xamlV2Rx.Matches(content)) referencedV2Keys.Add(m.Groups[1].Value);
            foreach (Match m in csV2Rx.Matches(content)) referencedV2Keys.Add(m.Groups[1].Value);
        }

        var missingRefAr = referencedV2Keys.Where(k => !ar.ContainsKey(k)).ToList();
        var missingRefEn = referencedV2Keys.Where(k => !en.ContainsKey(k)).ToList();

        Check("V2-i18n: v2.ar.json and v2.en.json have identical keys, format placeholders, and cover all V2 C#/XAML references",
            ar.Count > 0
            && missingInEn.Count == 0
            && missingInAr.Count == 0
            && phMismatches.Count == 0
            && missingRefAr.Count == 0
            && missingRefEn.Count == 0,
            $"count={ar.Count}, refCount={referencedV2Keys.Count}, missingRefAr=[{string.Join(",", missingRefAr)}], missingRefEn=[{string.Join(",", missingRefEn)}]");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    static bool ContainsSequence(IReadOnlyList<string> list, params string[] seq)
    {
        for (int i = 0; i <= list.Count - seq.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < seq.Length; j++)
            {
                if (list[i + j] != seq[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "GDM", "src", "GDM")))
            dir = dir.Parent;
        return dir?.FullName ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    sealed class FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    sealed class ThrottledCallbackStream(ReadOnlyMemory<byte> data, Action<long> onProgress) : Stream
    {
        int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onProgress(_pos);
            cancellationToken.ThrowIfCancellationRequested();
            if (_pos >= data.Length) return 0;
            int chunk = Math.Min(buffer.Length, Math.Min(16387, data.Length - _pos));
            data.Span.Slice(_pos, chunk).CopyTo(buffer.Span);
            _pos += chunk;
            await Task.Yield();
            return chunk;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
