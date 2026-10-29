using System.Text.RegularExpressions;

namespace Golan.Gdm.Core;

public enum RefreshMatch { None, ByName, BySize }

/// <summary>
/// IDM "Refresh download address": when a link expires, the user opens the source page and clicks the download
/// button again. The browser's new download is matched to the waiting item, which takes the fresh address and
/// resumes from where it stopped instead of starting a second copy.
/// </summary>
public sealed partial class AddressRefresh
{
    /// <summary>How long Golan waits for the new click before giving up.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Below this size, an equal size alone is too weak a sign that two different names are the same file.</summary>
    public const long MinSizeOnlyMatch = 1024 * 1024;

    readonly object _lock = new();
    Guid? _itemId;
    DateTime _untilUtc;

    public event Action<Guid>? Ended;

    public void Begin(Guid itemId, DateTime nowUtc)
    {
        Guid? previous;
        lock (_lock)
        {
            previous = _itemId;
            _itemId = itemId;
            _untilUtc = nowUtc + Window;
        }
        if (previous is { } p && p != itemId) Ended?.Invoke(p);
    }

    /// <summary>The item waiting for a new address, or null when nothing waits or the wait ran out.</summary>
    public Guid? Pending(DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_itemId != null && nowUtc > _untilUtc) _itemId = null;
            return _itemId;
        }
    }

    public TimeSpan Remaining(DateTime nowUtc)
    {
        lock (_lock) return _itemId == null || nowUtc > _untilUtc ? TimeSpan.Zero : _untilUtc - nowUtc;
    }

    public void Cancel(Guid? onlyFor = null)
    {
        Guid? ended;
        lock (_lock)
        {
            if (_itemId == null || (onlyFor != null && _itemId != onlyFor)) return;
            ended = _itemId;
            _itemId = null;
        }
        Ended?.Invoke(ended.Value);
    }

    /// <summary>Only ordinary web downloads can take a new address (not videos or torrents, which refresh themselves).</summary>
    public static bool CanRefresh(DownloadItem item)
        => item.Kind == DownloadKind.Http && item.Status != DownloadStatus.Completed && FileNames.IsHttpUrl(item.Url);

    /// <summary>
    /// Is the browser's new download the same file as the waiting item? A known size that differs always means
    /// another file (so a changed file is never glued onto the old part). Same name wins; otherwise an equal,
    /// non-trivial size does (sites that give every link a random name).
    /// </summary>
    public static RefreshMatch Match(DownloadItem item, string incomingName, long incomingSize)
    {
        bool sizesKnown = item.TotalSize > 0 && incomingSize > 0;
        if (sizesKnown && item.TotalSize != incomingSize) return RefreshMatch.None;
        var a = NameKey(item.FileName);
        if (a.Length > 0 && a == NameKey(incomingName)) return RefreshMatch.ByName;
        if (sizesKnown && incomingSize >= MinSizeOnlyMatch) return RefreshMatch.BySize;
        return RefreshMatch.None;
    }

    /// <summary>File name without case, surrounding spaces or the " (1)" a duplicate copy gets.</summary>
    public static string NameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim();
        var ext = Path.GetExtension(n);
        var stem = n[..^ext.Length];
        stem = CopySuffix().Replace(stem, "");
        return (stem.Trim() + ext).ToLowerInvariant();
    }

    [GeneratedRegex(@"\s*[\(\[]\d{1,3}[\)\]]$")]
    private static partial Regex CopySuffix();

    /// <summary>Puts the fresh address and session (cookies, agent, referrer) on the item; the downloaded parts stay.</summary>
    public static void Apply(DownloadItem item, string url, string referrer, string cookies, string userAgent, string pageUrl = "")
    {
        item.Url = url.Trim();
        item.SanitizeMegaUrlIfNeeded();
        if (!string.IsNullOrWhiteSpace(referrer)) item.Referrer = referrer;
        if (!string.IsNullOrWhiteSpace(userAgent)) item.UserAgent = userAgent;
        if (!string.IsNullOrWhiteSpace(pageUrl)) item.ParentPage = pageUrl;
        if (!string.IsNullOrWhiteSpace(cookies))
        {
            item.Cookies = cookies;
            item.CookieHost = item.Host;
        }
        item.AddressRefreshed = true;
        item.ErrorMessage = "";
        item.RetryCount = 0;
        if (item.Status == DownloadStatus.Error) item.Status = DownloadStatus.Paused;
    }

    /// <summary>The page to reopen so the user can click the download again (parent page first, then referrer).</summary>
    public static string SourcePage(DownloadItem item)
        => FileNames.IsHttpUrl(item.ParentPage) ? item.ParentPage
         : FileNames.IsHttpUrl(item.Referrer) ? item.Referrer
         : "";
}
