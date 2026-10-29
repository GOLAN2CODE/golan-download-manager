using Golan.Gdm.Core.Resolvers;

namespace Golan.Gdm.Core;

public sealed partial class AppSettings
{
    /// <summary>
    /// "Smart mode": videos sent from the browser start at once with the last chosen quality, format and folder,
    /// without showing the options window (what users love in popular video downloaders). Off by default.
    /// </summary>
    public bool VideoSmartMode { get; set; }

    List<string> _defaultSubtitleLangs = [];
    int _defaultMp3Bitrate = 192;

    /// <summary>Default subtitle language codes (up to 10 valid language tags) for video downloads.</summary>
    public List<string> DefaultSubtitleLangs
    {
        get => _defaultSubtitleLangs;
        set => _defaultSubtitleLangs = (value ?? [])
            .Where(MediaArgs.IsValidLangCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    /// <summary>Include auto-generated subtitles (`--write-auto-subs`) by default.</summary>
    public bool DefaultIncludeAutoSubs { get; set; }

    /// <summary>Embed subtitles into the output container (`--embed-subs`) by default.</summary>
    public bool DefaultEmbedSubs { get; set; }

    /// <summary>Write standalone subtitle files (`--write-subs`) by default.</summary>
    public bool DefaultWriteSubs { get; set; }

    /// <summary>Convert subtitles to `.srt` (`--convert-subs srt`) by default.</summary>
    public bool DefaultConvertSubsToSrt { get; set; } = true;

    /// <summary>Write video thumbnail file (`--write-thumbnail`) by default.</summary>
    public bool DefaultWriteThumbnail { get; set; }

    /// <summary>Embed thumbnail into the output media file (`--embed-thumbnail`) by default.</summary>
    public bool DefaultEmbedThumbnail { get; set; }

    /// <summary>Embed chapter markers (`--embed-chapters`) by default.</summary>
    public bool DefaultEmbedChapters { get; set; }

    /// <summary>Split video into separate files per chapter (`--split-chapters`) by default.</summary>
    public bool DefaultSplitChapters { get; set; }

    /// <summary>Default MP3 audio bitrate in kbps (`128`, `192`, or `320`).</summary>
    public int DefaultMp3Bitrate
    {
        get => _defaultMp3Bitrate;
        set => _defaultMp3Bitrate = value is 128 or 192 or 320 ? value : 192;
    }

    /// <summary>Embed media metadata tags (`--embed-metadata`) by default.</summary>
    public bool DefaultEmbedMetadata { get; set; }

    /// <summary>Builds a <see cref="MediaOptions"/> instance populated from the user's configured defaults.</summary>
    public MediaOptions CreateDefaultMediaOptions(string? quality = null) => new()
    {
        Quality = string.IsNullOrWhiteSpace(quality) ? DefaultVideoQuality : quality,
        SubtitleLangs = DefaultSubtitleLangs.Where(MediaArgs.IsValidLangCode).Take(10).ToList(),
        IncludeAutoSubs = DefaultIncludeAutoSubs,
        EmbedSubs = DefaultEmbedSubs,
        WriteSubs = DefaultWriteSubs,
        ConvertSubsToSrt = DefaultConvertSubsToSrt,
        WriteThumbnail = DefaultWriteThumbnail,
        EmbedThumbnail = DefaultEmbedThumbnail,
        EmbedChapters = DefaultEmbedChapters,
        SplitChapters = DefaultSplitChapters,
        Mp3BitrateKbps = DefaultMp3Bitrate is 128 or 192 or 320 ? DefaultMp3Bitrate : 192,
        EmbedMetadata = DefaultEmbedMetadata,
    };
}
