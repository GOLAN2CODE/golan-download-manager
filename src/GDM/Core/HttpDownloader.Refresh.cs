using System.Net;
using System.Net.Http.Headers;

namespace Golan.Gdm.Core;

public sealed partial class HttpDownloader
{
    /// <summary>Bytes compared per sample when a refreshed address must prove it serves the same file.</summary>
    public const int RefreshSampleSize = 64 * 1024;

    /// <summary>
    /// Before resuming, ask the server what it serves now. Mirrors, CDN nodes and refreshed addresses ("refresh
    /// download address") often carry another version tag for the very same file. The size must match, and unless
    /// the version tag (or date) is identical, a few already-downloaded pieces are fetched again and compared byte for
    /// byte. Only then does the item adopt the server's tags and keep its progress; any difference means another file
    /// (start over). When the server cannot be asked right now, the resume goes ahead (If-Range still guards it).
    /// </summary>
    async Task VerifyBeforeResumeAsync(CancellationToken ct)
    {
        ProbeResult p;
        try
        {
            var uri = CurrentUri;
            using var gate = await HostGate.AcquireAsync(uri.Host, Options.ServerLimitFor(uri.Host), ct).ConfigureAwait(false);
            using var resp = await SendProbeWithRetryAsync(uri, ContextFor(_item), new RangeHeaderValue(0, 0), ct).ConfigureAwait(false);
            p = ReadProbe(resp, uri.ToString());
            await DrainProbeAsync(resp, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DownloadException ex) when (ex.Kind == FailKind.Challenge || _item.AddressRefreshed) { throw; }
        catch (Exception ex)
        {
            Log.Write($"resume check skipped for {_item.FileName}: {ex.Message}");
            return;
        }
        if (p.Size != _item.TotalSize || !p.Resumable)
            throw new DownloadException(Loc.T("core.http.err_remote_changed"), FailKind.Mismatch);

        bool sameStrongTag = p.ETag.Length > 0 && !p.ETag.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
                             && string.Equals(p.ETag, _item.ETag, StringComparison.Ordinal);
        bool sameDate = p.ETag.Length == 0 && _item.ETag.Length == 0 && p.LastModified != null && p.LastModified == _item.LastModified;
        if (!sameStrongTag && !sameDate)
        {
            foreach (var (from, length) in RefreshSamples(_item))
                if (!await SampleMatchesAsync(p.FinalUri, from, length, ct).ConfigureAwait(false))
                {
                    Log.Write($"the server now serves different bytes at {from}: starting over {_item.FileName}");
                    throw new DownloadException(Loc.T("core.http.err_remote_changed"), FailKind.Mismatch);
                }
        }

        _item.ETag = p.ETag;
        _item.LastModified = p.LastModified;
        _item.ResolvedUrl = p.FinalUri.ToString();
        _item.AddressRefreshed = false;
        lock (_reResolveLock)
        {
            _reResolveTask = null;
            _reResolvedUrl = null;
        }
    }

    /// <summary>Up to three already-downloaded pieces: the file's first bytes and the last bytes of the biggest parts.</summary>
    public static List<(long From, int Length)> RefreshSamples(DownloadItem item)
    {
        var result = new List<(long, int)>();
        List<Segment> segs;
        lock (item.SegLock) segs = item.Segments.Where(s => s.Downloaded > 0).Select(s => new Segment { Start = s.Start, End = s.End, Downloaded = s.Downloaded }).ToList();

        var head = segs.FirstOrDefault(s => s.Start == 0);
        if (head != null) result.Add((0, (int)Math.Min(RefreshSampleSize, head.Downloaded)));
        foreach (var s in segs.OrderByDescending(s => s.Downloaded))
        {
            if (result.Count >= 3) break;
            int len = (int)Math.Min(RefreshSampleSize, s.Downloaded);
            long from = s.Position - len;
            if (result.Any(r => from < r.Item1 + r.Item2 && r.Item1 < from + len)) continue; // overlaps a chosen sample
            result.Add((from, len));
        }
        return result;
    }

    async Task<bool> SampleMatchesAsync(Uri uri, long from, int length, CancellationToken ct)
    {
        var local = new byte[length];
        using (var fs = new FileStream(_item.TempPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (fs.Length < from + length) return false;
            fs.Position = from;
            await fs.ReadExactlyAsync(local, ct).ConfigureAwait(false);
        }

        using var resp = await SendAsync(uri, ContextFor(_item), new RangeHeaderValue(from, from + length - 1), null, ct).ConfigureAwait(false);
        EnsureOk(resp);
        if (resp.StatusCode != HttpStatusCode.PartialContent || HasContentEncoding(resp)) return false;
        var cr = resp.Content.Headers.ContentRange;
        if (cr is null || cr.From != from || cr.To != from + length - 1) return false;

        var remote = new byte[length];
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try { await stream.ReadExactlyAsync(remote, ct).ConfigureAwait(false); }
        catch (EndOfStreamException) { return false; }
        return remote.AsSpan().SequenceEqual(local);
    }
}
