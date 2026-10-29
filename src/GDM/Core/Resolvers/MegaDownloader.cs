using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Golan.Gdm.Core.Resolvers;

public sealed class MegaDownloader
{
    const int BufferSize = 64 * 1024;
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    readonly DownloadItem _item;
    readonly byte[] _rawKey32;
    readonly SpeedLimiter _limiter;
    readonly HttpMessageInvoker? _http;
    readonly bool _verifyMac;

    public MegaDownloader(
        DownloadItem item,
        byte[] rawKey32,
        SpeedLimiter limiter,
        HttpMessageInvoker? http = null,
        bool verifyMac = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(rawKey32);
        ArgumentNullException.ThrowIfNull(limiter);
        if (rawKey32.Length != 32)
            throw new ArgumentException(Loc.T("err.mega.key"), nameof(rawKey32));

        _item = item;
        _rawKey32 = (byte[])rawKey32.Clone();
        _limiter = limiter;
        _http = http;
        _verifyMac = verifyMac;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var (aesKey16, nonce8) = MegaResolver.UnfoldKey(_rawKey32);
        byte[] expectedMetaMac = _rawKey32[24..32];

        _item.Status = DownloadStatus.Connecting;
        _item.LastTry = DateTime.Now;

        var safeSaveDir = MediaDownloader.EnsureSafeSaveDir(_item.SaveDir);
        _item.SaveDir = safeSaveDir;
        Directory.CreateDirectory(safeSaveDir);
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot))
        {
            var safeTempRoot = MediaDownloader.EnsureSafeSaveDir(DownloadItem.TempRoot);
            Directory.CreateDirectory(safeTempRoot);
        }

        if (string.IsNullOrWhiteSpace(_item.FileName))
            _item.FileName = "mega_download.bin";
        else
            _item.FileName = FileNames.Sanitize(Path.GetFileName(_item.FileName));

        if (_item.TotalSize > HttpDownloader.MaxDownloadSize)
            throw new DownloadException(Loc.T("core.http.err_file_too_large"));

        long resumeOffset = 0;
        if (File.Exists(_item.TempPath))
        {
            var fi = new FileInfo(_item.TempPath);
            if (fi.Length > 0 && (_item.TotalSize <= 0 || fi.Length < _item.TotalSize))
                resumeOffset = fi.Length;
        }

        if (resumeOffset == 0)
        {
            var reserved = PathRegistry.Reserve(_item.Id, _item.FullPath);
            _item.FileName = Path.GetFileName(reserved);
            _item.ResetProgress();
        }
        else
        {
            PathRegistry.Claim(_item.Id, _item.FullPath);
            _item.Downloaded = resumeOffset;
        }

        var targetUri = new Uri(_item.ResolvedUrl ?? _item.Url);
        bool ownsInvoker = _http is null;
        var invoker = _http ?? HostResolvers.CreateIsolatedInvoker();

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, targetUri);
            if (resumeOffset > 0)
                req.Headers.Range = new RangeHeaderValue(resumeOffset, null);

            using var resp = await HostResolvers.SendFollowHttpsAsync(invoker, req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new DownloadException(Loc.T("err.resolver.failed"));

            bool isPartial = resp.StatusCode == HttpStatusCode.PartialContent && resumeOffset > 0;
            long streamOffset = isPartial ? resumeOffset : 0;
            if (!isPartial && resumeOffset > 0)
            {
                _item.ResetProgress();
                MediaDownloader.TryDelete(_item.TempPath);
            }

            if (_item.TotalSize <= 0)
            {
                long contentLen = resp.Content.Headers.ContentRange?.Length
                                  ?? resp.Content.Headers.ContentLength
                                  ?? -1;
                if (contentLen > 0)
                    _item.TotalSize = isPartial && resp.Content.Headers.ContentRange?.Length is null
                        ? streamOffset + contentLen
                        : contentLen;
            }

            if (_item.TotalSize > HttpDownloader.MaxDownloadSize)
                throw new DownloadException(Loc.T("core.http.err_file_too_large"));

            long maxBytes = _item.TotalSize > 0 ? _item.TotalSize : HttpDownloader.MaxUnknownSize;

            _item.Resumable = true;
            _item.Status = DownloadStatus.Downloading;
            _item.ActiveConnections = 1;

            lock (_item.SegLock)
            {
                _item.Segments.Clear();
                _item.Segments.Add(new Segment
                {
                    Start = 0,
                    End = _item.TotalSize > 0 ? _item.TotalSize - 1 : -1,
                    Downloaded = streamOffset,
                    Active = true,
                    Info = Loc.T("core.http.seg_receiving"),
                });
            }
            var seg = _item.Segments[0];

            await using (var fs = new FileStream(
                _item.TempPath,
                isPartial ? FileMode.Open : FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite,
                BufferSize))
            {
                if (isPartial)
                    fs.Seek(streamOffset, SeekOrigin.Begin);

                await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                byte[] cipherBuf = new byte[BufferSize];
                byte[] plainBuf = new byte[BufferSize];

                while (true)
                {
                    int n = await ReadWithTimeoutAsync(netStream, cipherBuf, BufferSize, ct).ConfigureAwait(false);
                    if (n == 0) break;

                    if (streamOffset + n > maxBytes || streamOffset + n > HttpDownloader.MaxDownloadSize)
                    {
                        throw new DownloadException(_item.TotalSize > 0
                            ? Loc.T("core.http.err_stream_exceeded")
                            : Loc.T("core.http.err_unknown_size_exceeded"));
                    }

                    await _limiter.WaitAsync(n, ct).ConfigureAwait(false);
                    await _item.FileLimiter.WaitAsync(n, ct).ConfigureAwait(false);

                    MegaCrypto.TransformCtr(
                        aesKey16,
                        nonce8,
                        streamOffset,
                        cipherBuf.AsSpan(0, n),
                        plainBuf.AsSpan(0, n));

                    await fs.WriteAsync(plainBuf.AsMemory(0, n), ct).ConfigureAwait(false);

                    streamOffset += n;
                    lock (_item.SegLock) seg.Downloaded = streamOffset;
                    _item.AddDownloaded(n);
                }
            }

            if (_item.TotalSize > 0 && _item.Downloaded < _item.TotalSize)
                throw new DownloadException(Loc.T("core.http.err_incomplete"), FailKind.Retryable);

            if (_verifyMac)
            {
                byte[] actualMac;
                await using (var verifyFs = new FileStream(_item.TempPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize))
                {
                    actualMac = MegaCrypto.ComputeMetaMac(verifyFs, aesKey16, nonce8);
                }

                if (!CryptographicOperations.FixedTimeEquals(actualMac, expectedMetaMac))
                {
                    MediaDownloader.TryDelete(_item.TempPath);
                    MediaDownloader.TryDelete(_item.FullPath);
                    PathRegistry.Release(_item.Id);
                    throw new DownloadException(Loc.T("err.mega.decrypt"), FailKind.Mismatch);
                }
            }

            _item.Status = DownloadStatus.Merging;
            MarkOfTheWeb.Write(_item.TempPath, _item.ResolvedUrl ?? _item.Url, _item.Referrer);

            var target = PathRegistry.Reserve(_item.Id, _item.FullPath);
            if (File.Exists(target)) MediaDownloader.TryDelete(target);
            File.Move(_item.TempPath, target);
            _item.FileName = Path.GetFileName(target);
            if (_item.TotalSize <= 0) _item.TotalSize = _item.Downloaded;
            lock (_item.SegLock) _item.Segments.Clear();

            MarkOfTheWeb.Write(_item.FullPath, _item.ResolvedUrl ?? _item.Url, _item.Referrer);
            PathRegistry.Release(_item.Id);
        }
        finally
        {
            _item.ActiveConnections = 0;
            if (ownsInvoker) invoker.Dispose();
        }
    }

    static async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            return await stream.ReadAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(Loc.T("core.http.err_timeout"));
        }
    }
}
