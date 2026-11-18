using System.Text;

namespace Golan.Gdm.Core;

/// <summary>RFC 2397 data: URI decoder and downloader.</summary>
public static class DataUrlDownloader
{
    public static (string Mime, byte[] Data) Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new DownloadException(Loc.T("core.data.err_invalid")) { NonFallback = true };

        var trimmed = url.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new DownloadException(Loc.T("core.data.err_invalid")) { NonFallback = true };

        int comma = trimmed.IndexOf(',');
        if (comma < 5)
            throw new DownloadException(Loc.T("core.data.err_invalid")) { NonFallback = true };

        var meta = trimmed[5..comma];
        var payload = trimmed[(comma + 1)..];

        bool isBase64 = false;
        string mime = "text/plain";
        var parts = meta.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            var token = parts[i].Trim();
            if (i == 0 && token.Contains('/', StringComparison.Ordinal) && !token.Contains('=', StringComparison.Ordinal))
            {
                mime = token.ToLowerInvariant();
            }
            else if (token.Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                isBase64 = true;
            }
        }

        byte[] bytes;
        if (isBase64)
        {
            var clean = payload.Contains('%', StringComparison.Ordinal) ? DecodePercentAscii(payload) : payload;
            var sb = new StringBuilder(clean.Length);
            foreach (char c in clean)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
            }
            int mod = sb.Length % 4;
            if (mod == 1)
                throw new DownloadException(Loc.T("core.data.err_invalid")) { NonFallback = true };
            if (mod > 0)
                sb.Append('=', 4 - mod);

            try
            {
                bytes = Convert.FromBase64String(sb.ToString());
            }
            catch (FormatException)
            {
                throw new DownloadException(Loc.T("core.data.err_invalid")) { NonFallback = true };
            }
        }
        else
        {
            bytes = DecodePercentBytes(payload);
        }

        if (bytes.LongLength > HttpDownloader.MaxDownloadSize)
            throw new DownloadException(Loc.T("core.http.err_file_too_large")) { NonFallback = true };

        return (mime, bytes);
    }

    public static ProbeResult Probe(string url)
    {
        var (mime, data) = Parse(url);
        var name = "data" + FileNames.ExtensionForMime(mime);
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u);
        return new ProbeResult(u ?? new Uri("about:blank"), data.LongLength, false, name, mime, "", null);
    }

    public static async Task RunAsync(DownloadItem item, SpeedLimiter limiter, CancellationToken ct)
    {
        item.Status = DownloadStatus.Connecting;
        item.LastTry = DateTime.Now;
        MediaDownloader.EnsureSafeSaveDir(item.SaveDir);
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot))
            MediaDownloader.EnsureSafeSaveDir(DownloadItem.TempRoot);

        var (mime, data) = Parse(item.Url);

        Directory.CreateDirectory(item.SaveDir);
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot))
            Directory.CreateDirectory(DownloadItem.TempRoot);

        var ext = FileNames.ExtensionForMime(mime);
        if (!item.NameLocked)
        {
            if (string.IsNullOrWhiteSpace(item.FileName)
                || item.FileName.Equals("download", StringComparison.OrdinalIgnoreCase)
                || item.FileName.Equals("data", StringComparison.OrdinalIgnoreCase))
            {
                item.FileName = "data" + ext;
            }
            else if (string.IsNullOrEmpty(Path.GetExtension(item.FileName)))
            {
                item.FileName += ext;
            }
        }
        if (string.IsNullOrWhiteSpace(item.FileName))
            item.FileName = "data" + ext;

        item.FileName = FileNames.Sanitize(Path.GetFileName(item.FileName));
        var reserved = PathRegistry.Reserve(item.Id, item.FullPath);
        item.FileName = Path.GetFileName(reserved);

        try
        {
            MediaDownloader.TryDelete(item.TempPath);
            item.TotalSize = data.LongLength;
            item.Downloaded = 0;
            item.Resumable = false;
            item.Status = DownloadStatus.Downloading;

            const int chunkSize = 64 * 1024;
            await using (var fs = new FileStream(item.TempPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                int offset = 0;
                while (offset < data.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    int n = Math.Min(chunkSize, data.Length - offset);
                    await limiter.WaitAsync(n, ct).ConfigureAwait(false);
                    await item.FileLimiter.WaitAsync(n, ct).ConfigureAwait(false);
                    await fs.WriteAsync(data.AsMemory(offset, n), ct).ConfigureAwait(false);
                    offset += n;
                    item.AddDownloaded(n);
                }
            }

            item.Status = DownloadStatus.Merging;
            MarkOfTheWeb.Write(item.TempPath, item.Url, item.Referrer);
            File.Move(item.TempPath, reserved, overwrite: true);
            item.ActiveTempPath = "";
            MarkOfTheWeb.Write(item.FullPath, item.Url, item.Referrer);
        }
        finally
        {
            PathRegistry.Release(item.Id);
        }
    }

    static string DecodePercentAscii(string s)
    {
        var bytes = DecodePercentBytes(s);
        return Encoding.ASCII.GetString(bytes);
    }

    static byte[] DecodePercentBytes(string s)
    {
        var output = new List<byte>(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '%' && i + 2 < s.Length && TryHex(s[i + 1], out int hi) && TryHex(s[i + 2], out int lo))
            {
                output.Add((byte)((hi << 4) | lo));
                i += 3;
            }
            else if (c <= 0x7F)
            {
                output.Add((byte)c);
                i++;
            }
            else
            {
                var utf8 = Encoding.UTF8.GetBytes([c]);
                output.AddRange(utf8);
                i++;
            }
        }
        return output.ToArray();
    }

    static bool TryHex(char c, out int v)
    {
        if (c is >= '0' and <= '9') { v = c - '0'; return true; }
        if (c is >= 'A' and <= 'F') { v = c - 'A' + 10; return true; }
        if (c is >= 'a' and <= 'f') { v = c - 'a' + 10; return true; }
        v = 0;
        return false;
    }
}
