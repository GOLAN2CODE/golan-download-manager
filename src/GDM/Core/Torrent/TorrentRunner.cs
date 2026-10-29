using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace Golan.Gdm.Core.Torrent;

public static class TorrentRunner
{
    public static async Task DownloadAsync(
        DownloadItem item,
        AppSettings settings,
        Action<string> log,
        CancellationToken ct,
        bool loopbackOnly = false,
        IReadOnlyList<IPEndPoint>? explicitPeers = null)
    {
        // Prefer cached .torrent file path if already resolved (e.g. after magnet metadata was saved)
        string source = (!string.IsNullOrWhiteSpace(item.TorrentFilePath) && File.Exists(item.TorrentFilePath))
            ? item.TorrentFilePath!
            : !string.IsNullOrWhiteSpace(item.TorrentSource)
                ? item.TorrentSource!
                : !string.IsNullOrWhiteSpace(item.TorrentFilePath)
                    ? item.TorrentFilePath!
                    : item.Url;

        if (string.IsNullOrWhiteSpace(source))
            throw new DownloadException(TorrentText.T("torrent.error.invalidMagnet"));

        if (string.IsNullOrWhiteSpace(item.SaveDir))
            item.SaveDir = settings.DownloadRoot;
        Directory.CreateDirectory(item.SaveDir);

        bool isMagnet = MagnetValidator.IsMagnetUri(source);
        MagnetInfo? magnetInfo = null;
        MonoTorrent.Torrent? loadedTorrent = null;

        if (isMagnet)
        {
            if (!MagnetValidator.TryParse(source, out magnetInfo, out string? magnetErr) || magnetInfo is null)
                throw new DownloadException(magnetErr ?? TorrentText.T("torrent.error.invalidMagnet"));

            item.TorrentInfoHash = magnetInfo.InfoHashHex;
            if (string.IsNullOrWhiteSpace(item.FileName) && !string.IsNullOrWhiteSpace(magnetInfo.DisplayName))
                item.FileName = magnetInfo.DisplayName!;
        }
        else
        {
            if (!File.Exists(source))
                throw new DownloadException(TorrentText.T("torrent.error.fileNotFound", source));

            var fi = new FileInfo(source);
            if (fi.Length <= 0 || fi.Length > TorrentService.MaxTorrentFileSizeBytes)
                throw new DownloadException(TorrentText.T("torrent.error.invalidTorrentFile", source));

            try
            {
                loadedTorrent = await MonoTorrent.Torrent.LoadAsync(source);
            }
            catch (Exception ex)
            {
                throw new DownloadException(TorrentText.T("torrent.error.invalidTorrentFile", ex.Message));
            }

            var relPaths = loadedTorrent.Files.Select(f => f.Path).ToList();
            TorrentPaths.ValidateFileList(item.SaveDir, relPaths, loadedTorrent.Files.Count > 1 ? loadedTorrent.Name : null);

            string infoHashHex = (loadedTorrent.InfoHashes.V1OrV2.ToHex() ?? "").ToLowerInvariant();
            item.TorrentInfoHash = infoHashHex;
            if (TorrentFastResumeStore.IsValidInfoHashHex(infoHashHex))
            {
                try
                {
                    item.TorrentFilePath = TorrentFastResumeStore.SaveTorrentMetadataCopy(infoHashHex, source);
                }
                catch (IOException ex)
                {
                    Log.Write("torrent metadata cache copy failed: " + ex.Message);
                }
            }

            if (string.IsNullOrWhiteSpace(item.FileName) && !string.IsNullOrWhiteSpace(loadedTorrent.Name))
                item.FileName = FileNames.Sanitize(loadedTorrent.Name);
        }

        ClientEngine? ownedEngine = null;
        ClientEngine engine;
        if (loopbackOnly)
        {
            string isolatedCache = Path.Combine(TorrentFastResumeStore.TorrentCacheRoot, "loopback-" + item.Id.ToString("N")[..8]);
            ownedEngine = new ClientEngine(TorrentService.BuildEngineSettings(settings, loopbackOnly: true, customCacheDir: isolatedCache));
            engine = ownedEngine;
        }
        else
        {
            engine = await TorrentService.GetSharedEngineAsync(settings);
        }

        TorrentManager? manager = null;
        bool validationPassed = false;
        bool dataCompleted = false;
        bool motwApplied = false;

        try
        {
            int itemMaxRate = item.SpeedLimitKB > 0 ? item.SpeedLimitKB * 1024 : 0;
            var torrentSettings = new TorrentSettingsBuilder
            {
                AllowDht = !loopbackOnly && settings.TorrentDhtEnabled,
                AllowPeerExchange = !loopbackOnly && settings.TorrentPexEnabled,
                CreateContainingDirectory = loadedTorrent is { Files.Count: > 1 },
                MaximumConnections = Math.Clamp(settings.TorrentMaxConnections, 10, 500),
                MaximumDownloadRate = itemMaxRate,
                MaximumUploadRate = itemMaxRate
            }.ToSettings();

            item.Resumable = true;
            item.Status = DownloadStatus.Connecting;
            item.Stage = isMagnet ? TorrentText.T("torrent.status.metadata") : TorrentText.T("torrent.status.hashing");

            if (loadedTorrent != null)
            {
                if (!string.IsNullOrEmpty(item.TorrentInfoHash) &&
                    TorrentFastResumeStore.TryLoadFastResume(item.TorrentInfoHash, out byte[]? savedResume) &&
                    savedResume != null)
                {
                    try
                    {
                        string engineResumePath = engine.Settings.GetFastResumePath(loadedTorrent.InfoHashes);
                        Directory.CreateDirectory(Path.GetDirectoryName(engineResumePath)!);
                        await File.WriteAllBytesAsync(engineResumePath, savedResume, ct);
                    }
                    catch (IOException ex)
                    {
                        Log.Write("torrent fast-resume restore write failed: " + ex.Message);
                    }
                }

                manager = await engine.AddAsync(loadedTorrent, item.SaveDir, torrentSettings);
                TorrentPaths.ValidateMonoTorrentFiles(item.SaveDir, loadedTorrent.Name, manager.Files);
                validationPassed = true;

                long selectedSize = await TorrentSelection.ApplyToManagerAsync(manager, item);
                if (selectedSize <= 0)
                    throw new DownloadException(TorrentText.T("torrent.error.noFilesSelected"));
                item.TotalSize = selectedSize;
            }
            else if (magnetInfo != null)
            {
                manager = await engine.AddAsync(magnetInfo.MagnetLink, item.SaveDir, torrentSettings);
            }
            else
            {
                throw new DownloadException(TorrentText.T("torrent.error.invalidMagnet"));
            }

            TorrentService.RegisterActiveManager(item.Id, manager);
            await manager.StartAsync();

            if (explicitPeers != null && manager.Torrent?.IsPrivate != true)
            {
                var peers = explicitPeers
                    .Select(ep => new PeerInfo(new Uri($"ipv4://{ep.Address}:{ep.Port}")))
                    .ToList();
                if (peers.Count > 0)
                    await manager.AddPeersAsync(peers);
            }

            // If magnet link, wait for metadata with timeout before allowing piece writes
            if (isMagnet && !manager.HasMetadata)
            {
                item.Stage = TorrentText.T("torrent.status.metadata");
                int timeoutMin = Math.Clamp(settings.MagnetMetadataTimeoutMinutes, 1, 60);
                using var metaTimeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMin));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, metaTimeoutCts.Token);
                try
                {
                    await manager.WaitForMetadataAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (metaTimeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    throw new DownloadException(TorrentText.T("torrent.error.metadataTimeout"));
                }

                TorrentPaths.ValidateMonoTorrentFiles(item.SaveDir, manager.Torrent?.Name, manager.Files);
                validationPassed = true;

                // Cache downloaded magnet metadata .torrent file for fast resume
                if (TorrentFastResumeStore.IsValidInfoHashHex(item.TorrentInfoHash))
                {
                    try
                    {
                        string engineMetaPath = manager.MetadataPath;
                        if (!string.IsNullOrEmpty(engineMetaPath) && File.Exists(engineMetaPath))
                            item.TorrentFilePath = TorrentFastResumeStore.SaveTorrentMetadataCopy(item.TorrentInfoHash!, engineMetaPath);
                    }
                    catch (IOException ex)
                    {
                        Log.Write("magnet metadata cache copy failed: " + ex.Message);
                    }
                }

                if (manager.Torrent != null &&
                    (string.IsNullOrWhiteSpace(item.FileName) ||
                     string.Equals(item.FileName, item.TorrentInfoHash, StringComparison.OrdinalIgnoreCase)))
                {
                    item.FileName = FileNames.Sanitize(manager.Torrent.Name);
                }

                var prompt = TorrentService.MetadataReadyPromptAsync;
                if (prompt != null && manager.Torrent != null)
                {
                    bool confirmed = await prompt(item, manager.Torrent);
                    if (!confirmed)
                        throw new OperationCanceledException(ct);
                }

                long selectedSize = await TorrentSelection.ApplyToManagerAsync(manager, item);
                if (selectedSize <= 0)
                    throw new DownloadException(TorrentText.T("torrent.error.noFilesSelected"));
                item.TotalSize = selectedSize;
            }
            else if (isMagnet && manager.HasMetadata)
            {
                TorrentPaths.ValidateMonoTorrentFiles(item.SaveDir, manager.Torrent?.Name, manager.Files);
                validationPassed = true;
            }

            DateTime? seedingStartedUtc = null;
            long initialUploadedOffset = item.TorrentUploadedBytes;

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    if (dataCompleted)
                        break;
                    ct.ThrowIfCancellationRequested();
                }

                var state = manager.State;
                if (state == TorrentState.Error)
                {
                    string errReason = manager.Error?.Exception?.Message ?? "BitTorrent engine error";
                    throw new DownloadException(errReason);
                }

                bool isComplete = manager.Complete || state == TorrentState.Seeding || manager.PartialProgress >= 100.0;
                long totalSize = item.TotalSize > 0 ? item.TotalSize : (manager.Torrent?.Size ?? 0);
                if (totalSize > 0 && item.TotalSize != totalSize)
                    item.TotalSize = totalSize;

                double progressPct = isComplete ? 100.0 : Math.Clamp(manager.PartialProgress, 0.0, 100.0);
                long downloadedBytes = isComplete
                    ? totalSize
                    : (long)Math.Round(totalSize * (progressPct / 100.0));

                item.Downloaded = downloadedBytes;
                item.Speed = manager.Monitor.DownloadRate;
                item.TorrentUploadSpeed = manager.Monitor.UploadRate;
                item.TorrentUploadedBytes = initialUploadedOffset + manager.Monitor.DataBytesSent;
                item.TorrentSeeds = manager.Peers.Seeds;
                item.TorrentPeers = manager.Peers.Leechs + manager.Peers.Available;
                item.ActiveConnections = manager.OpenConnections;
                item.TorrentRatio = TorrentPolicy.ComputeRatio(
                    Math.Max(1, totalSize),
                    item.TorrentUploadedBytes);

                if (item.Speed > 0 && !isComplete && totalSize > downloadedBytes)
                {
                    item.Eta = TimeSpan.FromSeconds((totalSize - downloadedBytes) / item.Speed);
                }
                else
                {
                    item.Eta = null;
                }

                UpdateVisualSegments(item, manager, totalSize, downloadedBytes, isComplete);
                item.Stage = TorrentPolicy.MapStage(state, isComplete);

                if (isComplete)
                {
                    dataCompleted = true;
                    if (!motwApplied)
                    {
                        motwApplied = true;
                        try { await engine.DiskManager.FlushAsync(manager); }
                        catch (Exception ex) { Log.Write("torrent disk flush failed: " + ex.Message); }
                        ApplyMarkOfTheWebToFiles(manager, item.SaveDir, string.IsNullOrWhiteSpace(item.Url) ? source : item.Url, item.Referrer);
                    }

                    seedingStartedUtc ??= DateTime.UtcNow;
                    item.IsSeeding = true;
                    item.CompletedAt ??= DateTime.Now;
                    if (item.Status != DownloadStatus.Completed)
                        item.Status = DownloadStatus.Completed;

                    double ratioLimit = item.TorrentStopRatio ?? settings.StopSeedingAtRatio;
                    int minutesLimit = item.TorrentStopMinutes ?? settings.StopSeedingAfterMinutes;
                    TimeSpan seedingElapsed = DateTime.UtcNow - seedingStartedUtc.Value;

                    if (TorrentPolicy.ShouldStopSeeding(
                        Math.Max(1, totalSize),
                        item.TorrentUploadedBytes,
                        seedingElapsed,
                        ratioLimit,
                        minutesLimit))
                    {
                        log($"Seeding finished (ratio={item.TorrentRatio:0.00}, elapsed={seedingElapsed.TotalMinutes:0.1}m).");
                        break;
                    }
                }
                else
                {
                    var mapped = TorrentPolicy.MapStatus(state, isComplete: false);
                    if (mapped is DownloadStatus.Connecting or DownloadStatus.Downloading)
                        item.Status = mapped;
                }

                item.RefreshLive();

                try
                {
                    await Task.Delay(200, ct);
                }
                catch (OperationCanceledException) when (dataCompleted)
                {
                    break;
                }
            }
        }
        finally
        {
            item.IsSeeding = false;
            item.TorrentUploadSpeed = 0;
            item.Stage = "";

            if (manager != null)
            {
                TorrentService.UnregisterActiveManager(item.Id);

                if (validationPassed && (dataCompleted || manager.Complete) && !motwApplied)
                {
                    try { ApplyMarkOfTheWebToFiles(manager, item.SaveDir, string.IsNullOrWhiteSpace(item.Url) ? source : item.Url, item.Referrer); }
                    catch (Exception ex) { Log.Write("torrent MOTW apply failed: " + ex.Message); }
                }

                try
                {
                    if (manager.State != TorrentState.Stopped && manager.State != TorrentState.Stopping)
                        await manager.StopAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    Log.Write("torrent manager stop failed: " + ex.Message);
                }

                if (validationPassed)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(item.TorrentInfoHash) && manager.InfoHashes != null)
                        {
                            string engineResumePath = engine.Settings.GetFastResumePath(manager.InfoHashes);
                            if (File.Exists(engineResumePath))
                            {
                                byte[] resumeBytes = await File.ReadAllBytesAsync(engineResumePath);
                                TorrentFastResumeStore.SaveFastResume(item.TorrentInfoHash, resumeBytes);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write("torrent fast-resume save failed: " + ex.Message);
                    }

                    try
                    {
                        await engine.RemoveAsync(manager, RemoveMode.KeepAllData);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("torrent engine remove failed: " + ex.Message);
                    }
                }
                else
                {
                    TorrentFastResumeStore.DeleteFastResume(item.TorrentInfoHash);
                    try
                    {
                        await engine.RemoveAsync(manager, RemoveMode.CacheDataOnly);
                    }
                    catch (Exception ex)
                    {
                        Log.Write("torrent engine cache remove failed: " + ex.Message);
                    }
                }
            }

            if (ownedEngine != null)
            {
                try { await ownedEngine.StopAllAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { /* best-effort loopback engine stop */ }
                try { ownedEngine.Dispose(); }
                catch (Exception) { /* best-effort loopback engine dispose */ }
            }
        }
    }

    private static void ApplyMarkOfTheWebToFiles(TorrentManager manager, string saveDir, string sourceUrl, string? referrer = null)
    {
        if (manager.Files is null || string.IsNullOrWhiteSpace(saveDir)) return;
        var paths = manager.Files.Select(f => !string.IsNullOrEmpty(f.DownloadCompleteFullPath) ? f.DownloadCompleteFullPath : f.FullPath);
        ApplyMarkOfTheWebToPaths(paths, saveDir, sourceUrl, referrer);
    }

    internal static void ApplyMarkOfTheWebToPaths(IEnumerable<string?> rawPaths, string saveDir, string sourceUrl, string? referrer = null)
    {
        if (rawPaths is null || string.IsNullOrWhiteSpace(saveDir)) return;

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(saveDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            // Invalid saveDir path; skip MOTW write.
            return;
        }

        if (TorrentPaths.IsReparsePointPath(fullRoot)) return;
        string fullRootWithSep = fullRoot + Path.DirectorySeparatorChar;

        foreach (var rawPath in rawPaths)
        {
            if (string.IsNullOrEmpty(rawPath)) continue;

            try
            {
                string fullPath = Path.GetFullPath(rawPath);
                if (!fullPath.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.Length <= fullRootWithSep.Length)
                    continue;

                if (File.Exists(fullPath) && !TorrentPaths.IsReparsePointPath(fullPath))
                {
                    MarkOfTheWeb.Write(fullPath, sourceUrl, referrer);
                }
            }
            catch (Exception ex)
            {
                Log.Write("torrent file MOTW write failed: " + ex.Message);
            }
        }
    }

    private static void UpdateVisualSegments(DownloadItem item, TorrentManager manager, long totalSize, long downloadedBytes, bool isComplete)
    {
        if (totalSize <= 0) return;
        const int buckets = 16;
        long bucketLen = Math.Max(1, totalSize / buckets);

        lock (item.SegLock)
        {
            if (item.Segments.Count != buckets)
            {
                item.Segments.Clear();
                for (int i = 0; i < buckets; i++)
                {
                    long start = i * bucketLen;
                    long end = (i == buckets - 1) ? totalSize - 1 : ((i + 1) * bucketLen) - 1;
                    item.Segments.Add(new Segment { Start = start, End = end });
                }
            }

            for (int i = 0; i < buckets; i++)
            {
                var seg = item.Segments[i];
                long segLen = Math.Max(1, seg.End - seg.Start + 1);
                if (isComplete)
                {
                    seg.Downloaded = segLen;
                    seg.Active = false;
                }
                else
                {
                    long rem = Math.Max(0, downloadedBytes - seg.Start);
                    seg.Downloaded = Math.Clamp(rem, 0, segLen);
                    seg.Active = seg.Downloaded < segLen && manager.OpenConnections > 0;
                }
            }
        }
    }
}
