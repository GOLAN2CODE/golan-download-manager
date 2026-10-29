using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Connections;
using MonoTorrent.PiecePicking;

namespace Golan.Gdm.Core.Torrent;

public sealed class TorrentFileEntry : ObservableObject
{
    private bool _selected;
    private TorrentFilePriority _priority;

    public TorrentFileEntry(int index, string path, long length, TorrentFilePriority priority = TorrentFilePriority.Normal)
    {
        Index = index;
        Path = path;
        Length = length;
        _priority = priority;
        _selected = priority != TorrentFilePriority.DoNotDownload;
    }

    public int Index { get; }
    public string Path { get; }
    public long Length { get; }
    public string SizeText => Fmt.Size(Length);
    public bool IsExecutable => TorrentPaths.IsExecutable(Path);

    public bool Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                if (!value && _priority != TorrentFilePriority.DoNotDownload)
                {
                    _priority = TorrentFilePriority.DoNotDownload;
                    OnPropertyChanged(nameof(Priority));
                }
                else if (value && _priority == TorrentFilePriority.DoNotDownload)
                {
                    _priority = TorrentFilePriority.Normal;
                    OnPropertyChanged(nameof(Priority));
                }
            }
        }
    }

    public TorrentFilePriority Priority
    {
        get => _priority;
        set
        {
            if (Set(ref _priority, value))
            {
                bool shouldSelect = value != TorrentFilePriority.DoNotDownload;
                if (_selected != shouldSelect)
                {
                    _selected = shouldSelect;
                    OnPropertyChanged(nameof(Selected));
                }
            }
        }
    }
}

public static class TorrentSelection
{
    public static Priority ToMonoTorrentPriority(TorrentFilePriority priority) => priority switch
    {
        TorrentFilePriority.DoNotDownload => Priority.DoNotDownload,
        TorrentFilePriority.Low => Priority.Low,
        TorrentFilePriority.Normal => Priority.Normal,
        TorrentFilePriority.High => Priority.High,
        _ => Priority.Normal
    };

    public static TorrentFilePriority FromMonoTorrentPriority(Priority priority) => priority switch
    {
        Priority.DoNotDownload => TorrentFilePriority.DoNotDownload,
        Priority.Lowest or Priority.Low => TorrentFilePriority.Low,
        Priority.High or Priority.Highest or Priority.Immediate => TorrentFilePriority.High,
        _ => TorrentFilePriority.Normal
    };

    public static long ComputeSelectedSize(IEnumerable<TorrentFileEntry> entries)
    {
        if (entries is null) return 0;
        long sum = 0;
        foreach (var e in entries)
        {
            if (e.Selected && e.Priority != TorrentFilePriority.DoNotDownload)
                sum += e.Length;
        }
        return sum;
    }

    public static List<int> GetSelectedIndices(IEnumerable<TorrentFileEntry> entries)
    {
        var list = new List<int>();
        if (entries is null) return list;
        foreach (var e in entries)
        {
            if (e.Selected && e.Priority != TorrentFilePriority.DoNotDownload)
                list.Add(e.Index);
        }
        return list;
    }

    public static void SelectAll(IEnumerable<TorrentFileEntry> entries)
    {
        if (entries is null) return;
        foreach (var e in entries)
        {
            e.Priority = TorrentFilePriority.Normal;
            e.Selected = true;
        }
    }

    public static void SelectNone(IEnumerable<TorrentFileEntry> entries)
    {
        if (entries is null) return;
        foreach (var e in entries)
        {
            e.Priority = TorrentFilePriority.DoNotDownload;
            e.Selected = false;
        }
    }

    public static bool HasSelectedExecutable(IEnumerable<TorrentFileEntry> entries)
    {
        if (entries is null) return false;
        return entries.Any(e => e.Selected && e.Priority != TorrentFilePriority.DoNotDownload && e.IsExecutable);
    }

    public static async Task<long> ApplyToManagerAsync(TorrentManager manager, DownloadItem item)
    {
        if (manager.Files is null || manager.Files.Count == 0)
            return manager.Torrent?.Size ?? 0;

        HashSet<int>? selectedSet = item.TorrentSelectedFiles != null
            ? new HashSet<int>(item.TorrentSelectedFiles)
            : null;

        long selectedBytes = 0;
        for (int i = 0; i < manager.Files.Count; i++)
        {
            var file = manager.Files[i];
            TorrentFilePriority targetPrio = TorrentFilePriority.Normal;

            if (item.TorrentFilePriorities != null && item.TorrentFilePriorities.TryGetValue(i, out var explicitPrio))
            {
                targetPrio = explicitPrio;
            }
            else if (selectedSet != null)
            {
                targetPrio = selectedSet.Contains(i) ? TorrentFilePriority.Normal : TorrentFilePriority.DoNotDownload;
            }

            await manager.SetFilePriorityAsync(file, ToMonoTorrentPriority(targetPrio));
            if (targetPrio != TorrentFilePriority.DoNotDownload)
                selectedBytes += file.Length;
        }

        if (item.TorrentSequential)
        {
            var seqSettings = new PieceRequesterSettings(
                allowPrioritisation: true,
                allowRandomised: false,
                allowRarestFirst: false);
            await manager.ChangePickerAsync(Factories.Default.CreatePieceRequester(seqSettings));
        }

        return selectedBytes;
    }
}

public static class TorrentFastResumeStore
{
    public static string TorrentCacheRoot
    {
        get
        {
            string dir = Path.Combine(AppPaths.LocalDir, "torrent");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string FastResumeDirectory
    {
        get
        {
            string dir = Path.Combine(TorrentCacheRoot, "fast_resume");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string GetFastResumeFilePath(string infoHashHex)
    {
        ValidateInfoHashHex(infoHashHex);
        string normalized = infoHashHex.Trim().ToLowerInvariant();
        return Path.Combine(FastResumeDirectory, normalized + ".fresume");
    }

    public static string GetCachedTorrentFilePath(string infoHashHex)
    {
        ValidateInfoHashHex(infoHashHex);
        string normalized = infoHashHex.Trim().ToLowerInvariant();
        return Path.Combine(TorrentCacheRoot, normalized + ".torrent");
    }

    public static string SaveFastResume(string infoHashHex, byte[] encodedFastResume)
    {
        if (encodedFastResume is null || encodedFastResume.Length == 0)
            throw new ArgumentException("Fast-resume data cannot be empty.", nameof(encodedFastResume));

        string dest = GetFastResumeFilePath(infoHashHex);
        string tmp = dest + ".tmp";
        File.WriteAllBytes(tmp, encodedFastResume);
        File.Move(tmp, dest, overwrite: true);
        return dest;
    }

    public static bool TryLoadFastResume(string infoHashHex, out byte[]? encodedFastResume)
    {
        encodedFastResume = null;
        if (!IsValidInfoHashHex(infoHashHex))
            return false;

        string path = GetFastResumeFilePath(infoHashHex);
        if (!File.Exists(path))
            return false;

        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            if (!FastResume.TryLoad(ms, out var fr) || fr is null)
            {
                try { File.Delete(path); }
                catch (IOException) { /* best-effort delete of corrupt fast-resume file */ }
                return false;
            }
            encodedFastResume = bytes;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("fast resume load failed: " + ex.Message);
            try { File.Delete(path); }
            catch (IOException) { /* best-effort delete of corrupt fast-resume file */ }
            return false;
        }
    }

    public static void DeleteFastResume(string? infoHashHex)
    {
        if (string.IsNullOrWhiteSpace(infoHashHex) || !IsValidInfoHashHex(infoHashHex))
            return;

        try
        {
            string path = GetFastResumeFilePath(infoHashHex);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort fast-resume cleanup.
        }
    }

    public static string SaveTorrentMetadataCopy(string infoHashHex, string sourceTorrentPath)
    {
        ValidateInfoHashHex(infoHashHex);
        string dest = GetCachedTorrentFilePath(infoHashHex);
        if (!string.Equals(Path.GetFullPath(sourceTorrentPath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceTorrentPath, dest, overwrite: true);
        }
        return dest;
    }

    public static bool IsValidInfoHashHex(string? infoHashHex)
    {
        if (string.IsNullOrWhiteSpace(infoHashHex)) return false;
        string trimmed = infoHashHex.Trim();
        return (trimmed.Length == 40 || trimmed.Length == 64) && MagnetValidator.IsHex(trimmed);
    }

    private static void ValidateInfoHashHex(string infoHashHex)
    {
        if (!IsValidInfoHashHex(infoHashHex))
            throw new ArgumentException("Invalid infohash hex string.", nameof(infoHashHex));
    }
}

public static class TorrentText
{
    public static string T(string key) => Loc.T(key);
    public static string T(string key, params object?[] args) => Loc.T(key, args);
}

public static class TorrentPolicy
{
    public static DownloadStatus MapStatus(TorrentState state, bool isComplete)
    {
        if (isComplete || state == TorrentState.Seeding)
            return DownloadStatus.Completed;

        return state switch
        {
            TorrentState.Metadata or TorrentState.Starting or TorrentState.Hashing or TorrentState.HashingPaused or TorrentState.FetchingHashes
                => DownloadStatus.Connecting,
            TorrentState.Downloading
                => DownloadStatus.Downloading,
            TorrentState.Paused or TorrentState.Stopped or TorrentState.Stopping
                => DownloadStatus.Paused,
            TorrentState.Error
                => DownloadStatus.Error,
            _ => DownloadStatus.Connecting
        };
    }

    public static string MapStage(TorrentState state, bool isComplete)
    {
        if (state == TorrentState.Seeding)
            return TorrentText.T("torrent.status.seeding");
        if (isComplete)
            return "";

        return state switch
        {
            TorrentState.Metadata or TorrentState.FetchingHashes => TorrentText.T("torrent.status.metadata"),
            TorrentState.Hashing or TorrentState.HashingPaused => TorrentText.T("torrent.status.hashing"),
            TorrentState.Downloading => TorrentText.T("torrent.status.downloading"),
            TorrentState.Stopped or TorrentState.Paused => TorrentText.T("torrent.status.stopped"),
            _ => ""
        };
    }

    public static double ComputeRatio(long downloadedBytes, long uploadedBytes)
    {
        if (downloadedBytes <= 0) return 0.0;
        return (double)uploadedBytes / downloadedBytes;
    }

    public static bool ShouldStopSeeding(
        long downloadedBytes,
        long uploadedBytes,
        TimeSpan seedingElapsed,
        double ratioLimit,
        int minutesLimit)
    {
        if (ratioLimit <= 0.0 || minutesLimit <= 0)
            return true;

        double ratio = ComputeRatio(downloadedBytes, uploadedBytes);
        if (ratio >= ratioLimit)
            return true;

        if (seedingElapsed >= TimeSpan.FromMinutes(minutesLimit))
            return true;

        return false;
    }
}

public static class TorrentService
{
    public const long MaxTorrentFileSizeBytes = 16 * 1024 * 1024;

    private static readonly SemaphoreSlim EngineLock = new(1, 1);
    private static ClientEngine? _sharedEngine;
    private static int _cachedRandomListenPort;
    private static readonly ConcurrentDictionary<Guid, TorrentManager> ActiveManagers = new();

    /// <summary>
    /// Optional UI hook invoked when magnet metadata finishes downloading so the user can review files/priorities.
    /// Returning false cancels the download.
    /// </summary>
    public static Func<DownloadItem, MonoTorrent.Torrent, Task<bool>>? MetadataReadyPromptAsync { get; set; }

    public static int ResolveListenPort(int configuredPort)
    {
        if (configuredPort >= 1 && configuredPort <= 65535)
            return configuredPort;

        int existing = Volatile.Read(ref _cachedRandomListenPort);
        if (existing >= 49152 && existing <= 65535)
            return existing;

        int generated = Random.Shared.Next(49152, 65536);
        int prev = Interlocked.CompareExchange(ref _cachedRandomListenPort, generated, 0);
        return prev >= 49152 && prev <= 65535 ? prev : generated;
    }

    public static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static EngineSettings BuildEngineSettings(AppSettings settings, bool loopbackOnly = false, string? customCacheDir = null)
    {
        string cacheDir = customCacheDir ?? TorrentFastResumeStore.TorrentCacheRoot;
        Directory.CreateDirectory(cacheDir);

        int listenPort = loopbackOnly
            ? (settings.TorrentListenPort is >= 1 and <= 65535 ? settings.TorrentListenPort : 0)
            : ResolveListenPort(settings.TorrentListenPort);

        IPAddress bindAddr = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        var listenEndpoints = new Dictionary<string, IPEndPoint>
        {
            ["ipv4"] = new IPEndPoint(bindAddr, listenPort)
        };

        IPEndPoint? dhtEndpoint = (!loopbackOnly && settings.TorrentDhtEnabled)
            ? new IPEndPoint(IPAddress.Any, listenPort)
            : null;

        List<EncryptionType> encryption = settings.TorrentEncryptionPreferred
            ? [EncryptionType.RC4Full, EncryptionType.RC4Header, EncryptionType.PlainText]
            : [EncryptionType.PlainText, EncryptionType.RC4Header, EncryptionType.RC4Full];

        int maxRate = settings.SpeedLimitEnabled && settings.SpeedLimitKB > 0
            ? settings.SpeedLimitKB * 1024
            : 0;

        var builder = new EngineSettingsBuilder
        {
            CacheDirectory = cacheDir,
            ListenEndPoints = listenEndpoints,
            DhtEndPoint = dhtEndpoint,
            AllowLocalPeerDiscovery = !loopbackOnly && settings.TorrentLsdEnabled,
            AllowPortForwarding = !loopbackOnly && settings.TorrentPortForwarding,
            AutoSaveLoadDhtCache = !loopbackOnly && settings.TorrentDhtEnabled,
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            FastResumeMode = FastResumeMode.BestEffort,
            AllowedEncryption = encryption,
            MaximumConnections = Math.Clamp(settings.TorrentMaxConnections, 10, 1000),
            MaximumDownloadRate = maxRate,
            MaximumUploadRate = maxRate
        };

        return builder.ToSettings();
    }

    public static async Task<ClientEngine> GetSharedEngineAsync(AppSettings settings)
    {
        await EngineLock.WaitAsync();
        try
        {
            var engineSettings = BuildEngineSettings(settings);
            if (_sharedEngine is null)
            {
                _sharedEngine = new ClientEngine(engineSettings);
            }
            else
            {
                await _sharedEngine.UpdateSettingsAsync(engineSettings);
            }
            return _sharedEngine;
        }
        finally
        {
            EngineLock.Release();
        }
    }

    public static void RegisterActiveManager(Guid itemId, TorrentManager manager) =>
        ActiveManagers[itemId] = manager;

    public static void UnregisterActiveManager(Guid itemId) =>
        ActiveManagers.TryRemove(itemId, out _);

    public static bool TryGetActiveManager(Guid itemId, out TorrentManager? manager) =>
        ActiveManagers.TryGetValue(itemId, out manager);

    public static async Task RemoveTorrentArtifactsAsync(DownloadItem item, bool deleteDownloadedFiles)
    {
        IList<ITorrentManagerFile>? managerFiles = null;
        string? torrentName = null;

        if (ActiveManagers.TryRemove(item.Id, out var activeMgr))
        {
            managerFiles = activeMgr.Files;
            torrentName = activeMgr.Torrent?.Name;
            try
            {
                if (activeMgr.State != TorrentState.Stopped && activeMgr.State != TorrentState.Stopping)
                    await activeMgr.StopAsync(TimeSpan.FromSeconds(2));
                if (_sharedEngine != null)
                {
                    await _sharedEngine.RemoveAsync(activeMgr, RemoveMode.CacheDataOnly);
                }
            }
            catch (Exception ex)
            {
                Log.Write("torrent manager remove stop failed: " + ex.Message);
            }
        }

        TorrentFastResumeStore.DeleteFastResume(item.TorrentInfoHash);

        if (deleteDownloadedFiles)
        {
            await DeleteDownloadedTorrentDataSafelyAsync(item, managerFiles, torrentName);
        }
    }

    private static async Task DeleteDownloadedTorrentDataSafelyAsync(
        DownloadItem item,
        IList<ITorrentManagerFile>? managerFiles,
        string? torrentName)
    {
        if (string.IsNullOrWhiteSpace(item.SaveDir) || !Directory.Exists(item.SaveDir))
            return;

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(item.SaveDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (ArgumentException)
        {
            // Invalid SaveDir path; nothing safe to delete.
            return;
        }

        if (TorrentPaths.IsReparsePointPath(fullRoot))
            return;

        string fullRootWithSep = fullRoot + Path.DirectorySeparatorChar;
        var candidateRelativePaths = new List<string>();

        if (managerFiles is { Count: > 0 })
        {
            foreach (var mf in managerFiles)
            {
                if (!string.IsNullOrWhiteSpace(mf.Path))
                    candidateRelativePaths.Add(mf.Path);
            }
        }
        else
        {
            string? torrentFile = item.TorrentFilePath;
            if (string.IsNullOrWhiteSpace(torrentFile) && TorrentFastResumeStore.IsValidInfoHashHex(item.TorrentInfoHash))
            {
                string cached = TorrentFastResumeStore.GetCachedTorrentFilePath(item.TorrentInfoHash!);
                if (File.Exists(cached))
                    torrentFile = cached;
            }

            if (!string.IsNullOrWhiteSpace(torrentFile) && File.Exists(torrentFile))
            {
                try
                {
                    var fi = new FileInfo(torrentFile);
                    if (fi.Length <= MaxTorrentFileSizeBytes)
                    {
                        var t = await MonoTorrent.Torrent.LoadAsync(torrentFile);
                        torrentName ??= t.Name;
                        foreach (var tf in t.Files)
                        {
                            if (!string.IsNullOrWhiteSpace(tf.Path))
                                candidateRelativePaths.Add(tf.Path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("torrent metadata read during delete failed: " + ex.Message);
                }
            }

            if (candidateRelativePaths.Count == 0 && !string.IsNullOrWhiteSpace(item.FileName))
            {
                candidateRelativePaths.Add(item.FileName);
            }
        }

        var parentDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rel in candidateRelativePaths)
        {
            TryDeleteSanitizedRelativeFile(fullRoot, fullRootWithSep, rel, parentDirs);
            if (!string.IsNullOrWhiteSpace(torrentName) && candidateRelativePaths.Count > 1)
            {
                TryDeleteSanitizedRelativeFile(fullRoot, fullRootWithSep, Path.Combine(torrentName, rel), parentDirs);
            }
        }

        // Delete empty child directories strictly under SaveDir (never SaveDir itself)
        foreach (string dir in parentDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (dir.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(dir, fullRoot, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(dir) &&
                    !TorrentPaths.IsReparsePointPath(dir) &&
                    !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir, recursive: false);
                }
            }
            catch (IOException)
            {
                // Directory may still be in use or non-empty.
            }
        }
    }

    private static void TryDeleteSanitizedRelativeFile(
        string fullRoot,
        string fullRootWithSep,
        string relativePath,
        HashSet<string> parentDirs)
    {
        if (!TorrentPaths.TrySanitize(fullRoot, relativePath, out string target, out _))
            return;

        if (!target.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, fullRoot, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (File.Exists(target) && !TorrentPaths.IsReparsePointPath(target))
            {
                File.Delete(target);
            }

            string? dir = Path.GetDirectoryName(target);
            while (!string.IsNullOrEmpty(dir) &&
                   dir.StartsWith(fullRootWithSep, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(dir, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                parentDirs.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (IOException ex)
        {
            Log.Write("torrent file delete failed: " + ex.Message);
        }
    }
}
