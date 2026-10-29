using System.Text.Json.Serialization;

namespace Golan.Gdm.Core;

public enum HashVerifyState
{
    None,
    Verifying,
    Verified,
    Mismatch,
    Error,
}

public enum ArchiveExtractState
{
    None,
    WaitingForParts,
    Extracting,
    Extracted,
    SkippedNo7Zip,
    Failed,
}

public sealed partial class DownloadItem
{
    string _expectedHash = "";
    /// <summary>Expected digest (with optional algorithm prefix like "sha256:..." or raw hex).</summary>
    public string ExpectedHash
    {
        get => _expectedHash;
        set => Set(ref _expectedHash, value?.Trim() ?? "");
    }

    HashVerifyState _hashState = HashVerifyState.None;
    /// <summary>Current checksum verification state.</summary>
    public HashVerifyState HashState
    {
        get => _hashState;
        set
        {
            if (Set(ref _hashState, value))
            {
                OnPropertyChanged(nameof(HashStatusText));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    string _calculatedHash = "";
    /// <summary>Hex digest computed on the downloaded file.</summary>
    public string CalculatedHash
    {
        get => _calculatedHash;
        set => Set(ref _calculatedHash, value ?? "");
    }

    bool? _autoExtract;
    /// <summary>Per-item auto-extract flag; null follows <see cref="AppSettings.AutoExtractEnabled"/>.</summary>
    public bool? AutoExtract
    {
        get => _autoExtract;
        set => Set(ref _autoExtract, value);
    }

    ArchiveExtractState _extractState = ArchiveExtractState.None;
    /// <summary>Current post-download archive extraction state.</summary>
    public ArchiveExtractState ExtractState
    {
        get => _extractState;
        set
        {
            if (Set(ref _extractState, value))
            {
                OnPropertyChanged(nameof(ExtractStatusText));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    string _extractedPath = "";
    /// <summary>Target folder where the archive was extracted.</summary>
    public string ExtractedPath
    {
        get => _extractedPath;
        set => Set(ref _extractedPath, value ?? "");
    }

    string _extractError = "";
    /// <summary>Error message if archive extraction failed.</summary>
    public string ExtractError
    {
        get => _extractError;
        set
        {
            if (Set(ref _extractError, value ?? ""))
                OnPropertyChanged(nameof(ExtractStatusText));
        }
    }

    [JsonIgnore]
    public string HashStatusText => HashState switch
    {
        HashVerifyState.Verifying => Loc.T($"hash.status.verifying"),
        HashVerifyState.Verified => Loc.T($"hash.status.verified"),
        HashVerifyState.Mismatch => Loc.T($"hash.status.mismatch"),
        HashVerifyState.Error => Loc.T($"hash.status.error", ErrorMessage),
        _ => "",
    };

    [JsonIgnore]
    public string ExtractStatusText => ExtractState switch
    {
        ArchiveExtractState.WaitingForParts => Loc.T($"extract.status.waiting_parts"),
        ArchiveExtractState.Extracting => Loc.T($"extract.status.extracting"),
        ArchiveExtractState.Extracted => Loc.T($"extract.status.done"),
        ArchiveExtractState.SkippedNo7Zip => Loc.T($"extract.status.skipped_no_7z"),
        ArchiveExtractState.Failed => Loc.T($"extract.status.failed", ExtractError),
        _ => "",
    };
}
