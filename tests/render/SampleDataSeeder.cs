using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;
using Golan.Gdm.Integration;

namespace Golan.Gdm.Tests.Render;

public sealed class SeededEnvironment
{
    public required string GdmHome { get; init; }
    public required AppSettings Settings { get; init; }
    public required DownloadManager Manager { get; init; }
    public required DownloadItem ActiveHttpItem { get; init; }
    public required DownloadItem ActiveTorrentItem { get; init; }
    public required DownloadItem CompletedZipItem { get; init; }
    public required DownloadItem CompletedIsoItem { get; init; }
    public required DownloadItem PausedItem { get; init; }
    public required DownloadItem QueuedItem { get; init; }
    public required DownloadItem ErrorItem { get; init; }
    public required DownloadItem DuplicateIncomingItem { get; init; }
    public required string SampleRealFilePath { get; init; }
    public required string SampleRealFileSha256 { get; init; }
    public required List<TorrentFileEntry> SampleTorrentFiles { get; init; }
    public required List<BatchLink> SampleBatchLinks { get; init; }
    public required IdmParseResult SampleIdmParseResult { get; init; }
    public required MediaInfo SampleMediaInfo { get; init; }
    public required List<DetectedMedia> SampleDetectedMedia { get; init; }

    public void RefreshLocalizedStrings()
    {
        Categories.Use(Settings.CategoryDefs);

        lock (ActiveHttpItem.SegLock)
        {
            for (int i = 0; i < ActiveHttpItem.Segments.Count; i++)
            {
                var s = ActiveHttpItem.Segments[i];
                s.Info = s.Active ? Loc.T("core.http.seg_receiving") : Loc.T("core.http.seg_done");
            }
        }
        lock (ActiveTorrentItem.SegLock)
        {
            for (int i = 0; i < ActiveTorrentItem.Segments.Count; i++)
            {
                var s = ActiveTorrentItem.Segments[i];
                s.Info = s.Active ? Loc.T("core.http.seg_receiving") : Loc.T("core.http.seg_done");
            }
        }

        CompletedZipItem.LastResult = Loc.T("core.status.completed");
        CompletedIsoItem.LastResult = Loc.T("core.status.completed");
        ErrorItem.ErrorMessage = "HTTP 503";
        ErrorItem.LastResult = Loc.T("core.status.error", "HTTP 503");

        foreach (var item in Manager.Items)
            item.RefreshLive();
    }
}

public static class SampleDataSeeder
{
    public static SeededEnvironment Seed(string gdmHome)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Directory.CreateDirectory(AppPaths.LocalDir);

        var downloadsRoot = Path.Combine(gdmHome, "Downloads");
        Directory.CreateDirectory(downloadsRoot);

        // Create a real sample zip file on disk for HashWindow, PropertiesWindow, CompleteWindow
        var sampleZipPath = Path.Combine(downloadsRoot, "golan-sdk-2.2.0-win-x64.zip");
        using (var zfs = File.Create(sampleZipPath))
        using (var za = new ZipArchive(zfs, ZipArchiveMode.Create))
        {
            var e1 = za.CreateEntry("README.md");
            using (var w = new StreamWriter(e1.Open(), Encoding.UTF8))
                w.WriteLine("# Golan SDK 2.2.0\nSample archive for verification.");
            var e2 = za.CreateEntry("bin/gdm-cli.exe");
            using (var s = e2.Open())
                s.Write(new byte[4096]);
        }
        var sampleSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sampleZipPath)));

        var settings = new AppSettings
        {
            ClipboardMonitor = false,
            CloseToTray = false,
            ShowSpeedGraph = true,
            DownloadRoot = downloadsRoot,
            SpeedLimitEnabled = true,
            SpeedLimitKB = 4096,
            MaxConnections = 8,
            QuotaEnabled = true,
            QuotaMB = 5120,
            QuotaHours = 24,
            RemoteEnabled = true,
            RemoteBindAddress = "127.0.0.1",
            RemotePort = 17842,
            LastUpdateCheck = new DateTime(2026, 4, 15, 14, 30, 0, DateTimeKind.Utc),
            Logins =
            [
                new SiteLogin { Pattern = "*.example.com", User = "developer@example.com", Password = "secret" },
                new SiteLogin { Pattern = "releases.internal.org/*", User = "ci-bot", Password = "secret" },
            ],
            ServerLimits =
            [
                new ServerLimit { Pattern = "cdn.example.com", MaxConnections = 4 },
                new ServerLimit { Pattern = "mirror.ubuntu.com", MaxConnections = 2 },
            ],
            SpeedRules =
            [
                new SpeedRule { Enabled = true, Days = DaysMask.Weekdays, From = new TimeOnly(9, 0), To = new TimeOnly(17, 0), LimitKBps = 2048 },
                new SpeedRule { Enabled = true, Days = DaysMask.Weekend, From = new TimeOnly(18, 0), To = new TimeOnly(23, 30), LimitKBps = 8192 },
            ],
            RemoteDevices =
            [
                new PairedDevice
                {
                    Id = "dev-pixel-9",
                    Name = "Pixel 9 Pro",
                    TokenHash = "0123456789abcdef",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-5),
                    LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-12),
                },
                new PairedDevice
                {
                    Id = "dev-ipad-air",
                    Name = "iPad Air",
                    TokenHash = "fedcba9876543210",
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-14),
                    LastSeenAt = DateTimeOffset.UtcNow.AddHours(-3),
                },
            ],
        };
        settings.Normalize();

        var manager = new DownloadManager(settings);
        StopPrivateTimer(manager, "_timer");
        StopPrivateTimer(manager.Stats, "_flushTimer");

        // Seed SpeedHistory with a realistic waveform
        for (int i = 0; i < 60; i++)
        {
            double wave = 2.5 * 1024 * 1024 + Math.Sin(i * 0.25) * 1.4 * 1024 * 1024 + (i % 5) * 180 * 1024;
            manager.SpeedHistory.Add((long)Math.Max(256 * 1024, wave));
        }

        // Seed StatsStore with realistic multi-day download history
        for (int d = 0; d < 14; d++)
        {
            manager.Stats.Record(120L * 1024 * 1024 + d * 15L * 1024 * 1024, "https://releases.ubuntu.com/24.04/ubuntu.iso", "compressed");
            manager.Stats.Record(45L * 1024 * 1024, "https://cdn.example.com/videos/tutorial.mp4", "video");
            manager.Stats.Record(28L * 1024 * 1024, "https://dl.example.org/setup.exe", "programs");
            manager.Stats.RecordFileCompleted();
            for (int s = 0; s < 120; s++)
                manager.Stats.RecordActiveSecond();
        }

        // 1. Active HTTP Download
        long totalHttp = 1524L * 1024 * 1024;
        long doneHttp = 942L * 1024 * 1024;
        var activeHttp = new DownloadItem
        {
            Url = "https://releases.ubuntu.com/24.04/ubuntu-24.04.1-desktop-amd64.iso",
            Referrer = "https://ubuntu.com/download/desktop",
            ParentPage = "https://ubuntu.com/download/desktop",
            FileName = "ubuntu-24.04.1-desktop-amd64.iso",
            SaveDir = downloadsRoot,
            Category = "compressed",
            TotalSize = totalHttp,
            Downloaded = doneHttp,
            Status = DownloadStatus.Downloading,
            Speed = 14.8 * 1024 * 1024,
            Eta = TimeSpan.FromSeconds(39),
            ActiveConnections = 8,
            MaxConnections = 8,
            Resumable = true,
            ETag = "\"66c74e12-5f400000\"",
            AddedAt = DateTime.Now.AddMinutes(-4),
            LastTry = DateTime.Now.AddMinutes(-4),
            SpeedLimitKB = 4096,
        };
        long segLen = totalHttp / 8;
        for (int i = 0; i < 8; i++)
        {
            long start = i * segLen;
            long end = (i == 7) ? totalHttp - 1 : (start + segLen - 1);
            long segDone = (long)((end - start + 1) * (i < 3 ? 1.0 : 0.48));
            activeHttp.Segments.Add(new Segment
            {
                Start = start,
                End = end,
                Downloaded = segDone,
                Active = i >= 3,
                Info = i >= 3 ? Loc.T("core.http.seg_receiving") : Loc.T("core.http.seg_done"),
            });
        }

        // 2. Active Torrent Download
        long totalTor = 4200L * 1024 * 1024;
        long doneTor = 2730L * 1024 * 1024;
        var activeTorrent = new DownloadItem
        {
            Url = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Debian-12.7.0-amd64-DVD-1.iso",
            FileName = "Debian-12.7.0-amd64-DVD-1.iso",
            SaveDir = downloadsRoot,
            Category = "compressed",
            Kind = DownloadKind.Torrent,
            TotalSize = totalTor,
            Downloaded = doneTor,
            Status = DownloadStatus.Downloading,
            Speed = 8.4 * 1024 * 1024,
            Eta = TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(55)),
            ActiveConnections = 24,
            Resumable = true,
            TorrentInfoHash = "0123456789abcdef0123456789abcdef01234567",
            TorrentSeeds = 42,
            TorrentPeers = 19,
            TorrentRatio = 0.33,
            TorrentUploadSpeed = 640 * 1024,
            TorrentUploadedBytes = 890L * 1024 * 1024,
            TorrentSequential = true,
            AddedAt = DateTime.Now.AddMinutes(-18),
        };
        long torSegLen = totalTor / 4;
        for (int i = 0; i < 4; i++)
        {
            long start = i * torSegLen;
            long end = (i == 3) ? totalTor - 1 : (start + torSegLen - 1);
            activeTorrent.Segments.Add(new Segment
            {
                Start = start,
                End = end,
                Downloaded = (long)((end - start + 1) * 0.65),
                Active = true,
                Info = Loc.T("core.http.seg_receiving"),
            });
        }

        // 3. Completed Zip Download (real file on disk)
        var zipLen = new FileInfo(sampleZipPath).Length;
        var completedZip = new DownloadItem
        {
            Url = "https://cdn.example.com/releases/golan-sdk-2.2.0-win-x64.zip",
            Referrer = "https://example.com/downloads",
            ParentPage = "https://example.com/downloads",
            FileName = Path.GetFileName(sampleZipPath),
            SaveDir = downloadsRoot,
            Category = "compressed",
            TotalSize = zipLen,
            Downloaded = zipLen,
            Status = DownloadStatus.Completed,
            Resumable = true,
            ExpectedHash = "sha256:" + sampleSha256,
            AutoExtract = true,
            AddedAt = DateTime.Now.AddHours(-2),
            CompletedAt = DateTime.Now.AddHours(-1).AddMinutes(-58),
            LastTry = DateTime.Now.AddHours(-2),
            LastResult = Loc.T("core.status.completed"),
        };

        // 4. Completed Video Download
        var completedIso = new DownloadItem
        {
            Url = "https://cdn.example.com/courses/dotnet-10-masterclass-1080p.mp4",
            Referrer = "https://example.com/courses",
            FileName = "dotnet-10-masterclass-1080p.mp4",
            SaveDir = downloadsRoot,
            Category = "video",
            Kind = DownloadKind.Media,
            MediaQuality = "1080",
            TotalSize = 684L * 1024 * 1024,
            Downloaded = 684L * 1024 * 1024,
            Status = DownloadStatus.Completed,
            Resumable = true,
            AddedAt = DateTime.Now.AddDays(-1),
            CompletedAt = DateTime.Now.AddDays(-1).AddMinutes(12),
            LastResult = Loc.T("core.status.completed"),
        };

        // 5. Paused Download (in MainQueue)
        var pausedItem = new DownloadItem
        {
            Url = "https://dl.example.org/tools/blender-4.3.0-windows-x64.msi",
            Referrer = "https://example.org/download/",
            FileName = "blender-4.3.0-windows-x64.msi",
            SaveDir = downloadsRoot,
            Category = "programs",
            TotalSize = 340L * 1024 * 1024,
            Downloaded = 152L * 1024 * 1024,
            Status = DownloadStatus.Paused,
            Resumable = true,
            QueueId = QueueDef.MainId,
            QueuePosition = 1,
            AddedAt = DateTime.Now.AddMinutes(-45),
        };

        // 6. Queued Download (in MainQueue)
        var queuedItem = new DownloadItem
        {
            Url = "https://mirror.example.com/docs/architecture-handbook-2026.pdf",
            Referrer = "https://mirror.example.com/docs/",
            FileName = "architecture-handbook-2026.pdf",
            SaveDir = downloadsRoot,
            Category = "documents",
            TotalSize = 24L * 1024 * 1024,
            Downloaded = 0,
            Status = DownloadStatus.Queued,
            Resumable = true,
            QueueId = QueueDef.MainId,
            QueuePosition = 2,
            AddedAt = DateTime.Now.AddMinutes(-30),
        };

        // 7. Error Download
        var errorItem = new DownloadItem
        {
            Url = "https://files.example.net/archive/dataset-2026-q1.tar.gz",
            FileName = "dataset-2026-q1.tar.gz",
            SaveDir = downloadsRoot,
            Category = "compressed",
            TotalSize = 890L * 1024 * 1024,
            Downloaded = 120L * 1024 * 1024,
            Status = DownloadStatus.Error,
            ErrorMessage = "HTTP 503",
            LastResult = Loc.T("core.status.error", "HTTP 503"),
            Resumable = true,
            AddedAt = DateTime.Now.AddHours(-3),
        };

        // 8. Incoming duplicate item
        var dupIncoming = new DownloadItem
        {
            Url = completedZip.Url,
            Referrer = "https://example.com/mirror",
            FileName = completedZip.FileName,
            SaveDir = downloadsRoot,
            Category = "compressed",
            TotalSize = completedZip.TotalSize,
        };

        manager.Items.Clear();
        manager.Items.Add(activeHttp);
        manager.Items.Add(activeTorrent);
        manager.Items.Add(completedZip);
        manager.Items.Add(completedIso);
        manager.Items.Add(pausedItem);
        manager.Items.Add(queuedItem);
        manager.Items.Add(errorItem);

        var torrentEntries = new List<TorrentFileEntry>
        {
            new(0, "Debian-12.7.0-amd64/README.txt", 18432, TorrentFilePriority.High),
            new(1, "Debian-12.7.0-amd64/debian-12.7.0-amd64-DVD-1.iso", 3980L * 1024 * 1024, TorrentFilePriority.Normal),
            new(2, "Debian-12.7.0-amd64/tools/setup-verify.exe", 4200 * 1024, TorrentFilePriority.DoNotDownload),
            new(3, "Debian-12.7.0-amd64/SHA256SUMS", 4096, TorrentFilePriority.High),
        };

        var batchLinks = new List<BatchLink>
        {
            new("https://cdn.example.com/releases/golan-2.2.0-win-x64.zip", "Golan 2.2 Windows x64 Archive"),
            new("https://cdn.example.com/releases/golan-2.2.0-win-arm64.zip", "Golan 2.2 Windows ARM64 Archive"),
            new("https://cdn.example.com/releases/user-guide-2.2.pdf", "User Guide PDF"),
            new("https://cdn.example.com/releases/release-notes.txt", "Release Notes"),
        };

        var idmParseResult = new IdmParseResult(
            [
                new IdmCandidate(
                    "https://dl.example.com/packages/vscode-win32-x64-1.98.0.zip",
                    "https://dl.example.com/packages/vscode-win32-x64-1.98.0.zip",
                    "vscode-win32-x64-1.98.0.zip",
                    downloadsRoot,
                    "https://code.visualstudio.com/",
                    "", "", "", "", "VS Code x64",
                    138L * 1024 * 1024,
                    IsCompletedInSource: true,
                    DateTimeOffset.UtcNow.AddDays(-3)),
                new IdmCandidate(
                    "https://dl.example.com/packages/git-2.48.1-64-bit.exe",
                    "https://dl.example.com/packages/git-2.48.1-64-bit.exe",
                    "git-2.48.1-64-bit.exe",
                    downloadsRoot,
                    "https://git-scm.com/",
                    "", "", "", "", "Git for Windows",
                    65L * 1024 * 1024,
                    IsCompletedInSource: false,
                    DateTimeOffset.UtcNow.AddDays(-1)),
                new IdmCandidate(
                    completedZip.Url,
                    completedZip.Url,
                    completedZip.FileName,
                    downloadsRoot,
                    "", "", "", "", "", "Existing item",
                    completedZip.TotalSize,
                    IsCompletedInSource: true,
                    DateTimeOffset.UtcNow.AddDays(-2)),
            ],
            SkippedCount: 1);

        var mediaInfo = new MediaInfo
        {
            Title = "Building High-Performance Desktop Apps in .NET 10",
            Uploader = "DotNet Engineering Channel",
            Duration = 1845,
            Thumbnail = "",
            WebpageUrl = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            IsPlaylist = true,
        };
        mediaInfo.Qualities.AddRange([
            new MediaQuality { Key = "best", Label = "4K (2160p MP4)", Size = 820L * 1024 * 1024 },
            new MediaQuality { Key = "1080", Label = "1080p Full HD (MP4)", Size = 310L * 1024 * 1024 },
            new MediaQuality { Key = "720", Label = "720p HD (MP4)", Size = 145L * 1024 * 1024 },
            new MediaQuality { Key = "mp3", Label = "MP3 Audio", AudioOnly = true, Size = 28L * 1024 * 1024 },
        ]);
        mediaInfo.Subtitles.AddRange([
            new SubtitleTrack("ar", "العربية", IsAuto: false),
            new SubtitleTrack("en", "English", IsAuto: false),
            new SubtitleTrack("en-auto", "English (Auto)", IsAuto: true),
        ]);
        mediaInfo.Entries.AddRange([
            new PlaylistEntry { Title = "01 - Architecture & Pipeline Overview", Url = "https://example.com/watch?v=ep1", Duration = 612, Selected = true },
            new PlaylistEntry { Title = "02 - Segmented HTTP & Memory Pools", Url = "https://example.com/watch?v=ep2", Duration = 738, Selected = true },
            new PlaylistEntry { Title = "03 - Offscreen WPF Visual Verification", Url = "https://example.com/watch?v=ep3", Duration = 495, Selected = false },
        ]);

        var detectedMedia = new List<DetectedMedia>
        {
            new("https://cdn.example.com/hls/master-1080p.m3u8", "master-1080p.m3u8", 310L * 1024 * 1024),
            new("https://cdn.example.com/progressive/lecture-720p.mp4", "lecture-720p.mp4", 145L * 1024 * 1024),
        };

        var env = new SeededEnvironment
        {
            GdmHome = gdmHome,
            Settings = settings,
            Manager = manager,
            ActiveHttpItem = activeHttp,
            ActiveTorrentItem = activeTorrent,
            CompletedZipItem = completedZip,
            CompletedIsoItem = completedIso,
            PausedItem = pausedItem,
            QueuedItem = queuedItem,
            ErrorItem = errorItem,
            DuplicateIncomingItem = dupIncoming,
            SampleRealFilePath = sampleZipPath,
            SampleRealFileSha256 = sampleSha256,
            SampleTorrentFiles = torrentEntries,
            SampleBatchLinks = batchLinks,
            SampleIdmParseResult = idmParseResult,
            SampleMediaInfo = mediaInfo,
            SampleDetectedMedia = detectedMedia,
        };
        env.RefreshLocalizedStrings();
        return env;
    }

    public static void StopPrivateTimer(object target, string fieldName)
    {
        var f = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (f?.GetValue(target) is System.Windows.Threading.DispatcherTimer dt)
            dt.Stop();
        else if (f?.GetValue(target) is System.Threading.Timer t)
            t.Change(Timeout.Infinite, Timeout.Infinite);
    }
}
