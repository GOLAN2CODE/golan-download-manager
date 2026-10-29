using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Golan.Gdm.Core.Torrent;

namespace Golan.Gdm.Core;

public sealed partial class DownloadManager
{
    internal Task RunTorrentAsync(DownloadItem item, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(item.SaveDir))
            EnsureSafeDir(item.SaveDir);
        return TorrentRunner.DownloadAsync(item, Settings, msg => Log.Write($"[torrent {item.FileName}] {msg}"), ct);
    }

    public DownloadItem AddMagnet(
        string magnetUri,
        string? saveDir = null,
        bool startNow = true,
        bool sequential = false)
    {
        if (!MagnetValidator.TryParse(magnetUri, out var info, out string? err) || info is null)
            throw new DownloadException(err ?? TorrentText.T("torrent.error.invalidMagnet"));

        string displayName = !string.IsNullOrWhiteSpace(info.DisplayName)
            ? info.DisplayName!
            : info.InfoHashHex;

        string category = Categories.Detect(displayName, magnetUri);
        string resolvedSaveDir = !string.IsNullOrWhiteSpace(saveDir)
            ? saveDir!
            : Settings.FolderFor(category);
        EnsureSafeDir(resolvedSaveDir);
        Directory.CreateDirectory(resolvedSaveDir);

        var item = new DownloadItem
        {
            Url = magnetUri.Trim(),
            TorrentSource = magnetUri.Trim(),
            TorrentInfoHash = info.InfoHashHex,
            FileName = FileNames.Sanitize(displayName),
            Category = category,
            SaveDir = resolvedSaveDir,
            Kind = DownloadKind.Torrent,
            Resumable = true,
            TorrentSequential = sequential,
            TorrentStopRatio = Settings.StopSeedingAtRatio,
            TorrentStopMinutes = Settings.StopSeedingAfterMinutes,
            Status = DownloadStatus.Queued
        };

        Items.Insert(0, item);
        Save();
        if (startNow)
            Start(item);
        return item;
    }

    public async Task<DownloadItem> AddTorrentFileAsync(
        string torrentFilePath,
        string? saveDir = null,
        IReadOnlyList<TorrentFileEntry>? fileSelection = null,
        bool sequential = false,
        bool startNow = true)
    {
        if (string.IsNullOrWhiteSpace(torrentFilePath) || !File.Exists(torrentFilePath))
            throw new DownloadException(TorrentText.T("torrent.error.fileNotFound", torrentFilePath ?? ""));

        var fi = new FileInfo(torrentFilePath);
        if (fi.Length <= 0 || fi.Length > TorrentService.MaxTorrentFileSizeBytes)
            throw new DownloadException(TorrentText.T("torrent.error.invalidTorrentFile", torrentFilePath));

        MonoTorrent.Torrent torrent;
        try
        {
            torrent = await MonoTorrent.Torrent.LoadAsync(torrentFilePath);
        }
        catch (Exception ex)
        {
            throw new DownloadException(TorrentText.T("torrent.error.invalidTorrentFile", ex.Message));
        }

        string displayName = !string.IsNullOrWhiteSpace(torrent.Name)
            ? FileNames.Sanitize(torrent.Name)
            : Path.GetFileNameWithoutExtension(torrentFilePath);

        string category = Categories.Detect(displayName, torrentFilePath);
        string resolvedSaveDir = !string.IsNullOrWhiteSpace(saveDir)
            ? saveDir!
            : Settings.FolderFor(category);
        EnsureSafeDir(resolvedSaveDir);
        Directory.CreateDirectory(resolvedSaveDir);

        var relPaths = torrent.Files.Select(f => f.Path).ToList();
        TorrentPaths.ValidateFileList(resolvedSaveDir, relPaths, torrent.Files.Count > 1 ? torrent.Name : null);

        string infoHashHex = (torrent.InfoHashes.V1OrV2.ToHex() ?? "").ToLowerInvariant();
        string cachedPath = TorrentFastResumeStore.IsValidInfoHashHex(infoHashHex)
            ? TorrentFastResumeStore.SaveTorrentMetadataCopy(infoHashHex, torrentFilePath)
            : torrentFilePath;

        List<int>? selectedIndices = null;
        Dictionary<int, TorrentFilePriority>? priorities = null;
        long totalSize = torrent.Size;

        if (fileSelection is { Count: > 0 })
        {
            selectedIndices = TorrentSelection.GetSelectedIndices(fileSelection);
            priorities = fileSelection.ToDictionary(e => e.Index, e => e.Priority);
            totalSize = TorrentSelection.ComputeSelectedSize(fileSelection);
            if (totalSize <= 0)
                throw new DownloadException(TorrentText.T("torrent.error.noFilesSelected"));
        }

        var item = new DownloadItem
        {
            Url = torrentFilePath,
            TorrentSource = cachedPath,
            TorrentFilePath = cachedPath,
            TorrentInfoHash = infoHashHex,
            FileName = displayName,
            Category = category,
            SaveDir = resolvedSaveDir,
            TotalSize = totalSize,
            Kind = DownloadKind.Torrent,
            Resumable = true,
            TorrentSequential = sequential,
            TorrentSelectedFiles = selectedIndices,
            TorrentFilePriorities = priorities,
            TorrentStopRatio = Settings.StopSeedingAtRatio,
            TorrentStopMinutes = Settings.StopSeedingAfterMinutes,
            Status = DownloadStatus.Queued
        };

        Items.Insert(0, item);
        Save();
        if (startNow)
            Start(item);
        return item;
    }

    public Task CleanupTorrentItemAsync(DownloadItem item, bool deleteDownloadedFiles) =>
        TorrentService.RemoveTorrentArtifactsAsync(item, deleteDownloadedFiles);
}
