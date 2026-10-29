using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Golan.Gdm.Core;

public enum TorrentFilePriority
{
    DoNotDownload = 0,
    Low = 1,
    Normal = 2,
    High = 3
}

public sealed partial class DownloadItem
{
    // Persisted BitTorrent metadata & user choices
    public string? TorrentInfoHash { get; set; }
    public string? TorrentSource { get; set; }
    public string? TorrentFilePath { get; set; }
    public bool TorrentSequential { get; set; }
    public List<int>? TorrentSelectedFiles { get; set; }
    public Dictionary<int, TorrentFilePriority>? TorrentFilePriorities { get; set; }
    public long TorrentUploadedBytes { get; set; }
    public double? TorrentStopRatio { get; set; }
    public int? TorrentStopMinutes { get; set; }

    // Live UI state ([JsonIgnore])
    private int _torrentSeeds;
    private int _torrentPeers;
    private double _torrentRatio;
    private long _torrentUploadSpeed;
    private bool _isSeeding;

    [JsonIgnore]
    public int TorrentSeeds
    {
        get => _torrentSeeds;
        set => Set(ref _torrentSeeds, value);
    }

    [JsonIgnore]
    public int TorrentPeers
    {
        get => _torrentPeers;
        set => Set(ref _torrentPeers, value);
    }

    [JsonIgnore]
    public double TorrentRatio
    {
        get => _torrentRatio;
        set
        {
            if (Set(ref _torrentRatio, value))
                OnPropertyChanged(nameof(TorrentRatioText));
        }
    }

    [JsonIgnore]
    public long TorrentUploadSpeed
    {
        get => _torrentUploadSpeed;
        set
        {
            if (Set(ref _torrentUploadSpeed, value))
                OnPropertyChanged(nameof(TorrentUploadSpeedText));
        }
    }

    [JsonIgnore]
    public bool IsSeeding
    {
        get => _isSeeding;
        set => Set(ref _isSeeding, value);
    }

    [JsonIgnore]
    public bool IsTorrent => Kind == DownloadKind.Torrent;

    [JsonIgnore]
    public string TorrentRatioText => Fmt.Ratio(TorrentRatio);

    [JsonIgnore]
    public string TorrentUploadSpeedText => Fmt.Speed(TorrentUploadSpeed);
}
