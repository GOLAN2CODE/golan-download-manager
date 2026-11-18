using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core;

/// <summary>Passive FTP and FTPS (explicit AUTH TLS and implicit port 990) downloader with REST resume.</summary>
public sealed class FtpDownloader(DownloadItem item, SpeedLimiter limiter)
{
    const int BufferSize = 64 * 1024;

    public static async Task<ProbeResult> ProbeAsync(DownloadItem item, CancellationToken ct)
    {
        if (!Uri.TryCreate(item.Url.Trim(), UriKind.Absolute, out var uri) || !FileNames.IsFtpUrl(item.Url))
            throw new DownloadException(Loc.T("core.http.err_unsupported_scheme"));

        await using var session = await ConnectAndLoginAsync(item, uri, ct).ConfigureAwait(false);
        string remotePath = GetRemotePath(uri);

        long size = -1;
        var sizeReply = await session.SendCommandAsync("SIZE " + remotePath, ct).ConfigureAwait(false);
        if (sizeReply.Code == 213 && long.TryParse(sizeReply.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed >= 0)
            size = parsed;

        bool resumable = false;
        var restReply = await session.SendCommandAsync("REST 0", ct).ConfigureAwait(false);
        if (restReply.Code == 350)
            resumable = true;

        try { await session.SendCommandAsync("QUIT", ct).ConfigureAwait(false); }
        catch { /* ignore quit failure after probe */ }

        var name = FileNames.FromUrl(uri.ToString());
        return new ProbeResult(uri, size, resumable && size > 0, name, null, "", null);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(item.Url.Trim(), UriKind.Absolute, out var uri) || !FileNames.IsFtpUrl(item.Url))
            throw new DownloadException(Loc.T("core.http.err_unsupported_scheme"));

        item.Status = DownloadStatus.Connecting;
        item.LastTry = DateTime.Now;
        MediaDownloader.EnsureSafeSaveDir(item.SaveDir);
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot))
            MediaDownloader.EnsureSafeSaveDir(DownloadItem.TempRoot);

        Directory.CreateDirectory(item.SaveDir);
        if (!string.IsNullOrWhiteSpace(DownloadItem.TempRoot))
            Directory.CreateDirectory(DownloadItem.TempRoot);

        if (string.IsNullOrWhiteSpace(item.FileName))
            item.FileName = FileNames.FromUrl(uri.ToString());
        item.FileName = FileNames.Sanitize(Path.GetFileName(item.FileName));

        string remotePath = GetRemotePath(uri);
        using var gate = await HostGate.AcquireAsync(uri.Host, HttpDownloader.Options.ServerLimitFor(uri.Host), ct).ConfigureAwait(false);
        await using var session = await ConnectAndLoginAsync(item, uri, ct).ConfigureAwait(false);

        long remoteSize = -1;
        var sizeReply = await session.SendCommandAsync("SIZE " + remotePath, ct).ConfigureAwait(false);
        if (sizeReply.Code == 213 && long.TryParse(sizeReply.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedSize) && parsedSize >= 0)
        {
            if (parsedSize > HttpDownloader.MaxDownloadSize)
                throw new DownloadException(Loc.T("core.http.err_file_too_large"));
            remoteSize = parsedSize;
        }

        try
        {
            long resumeOffset = 0;
            string reservedPath;
            bool hasExistingTemp = !PathRegistry.IsClaimedByOther(item.Id, item.FullPath) && File.Exists(item.TempPath);
            if (hasExistingTemp && item.Downloaded > 0)
            {
                long existingLen = new FileInfo(item.TempPath).Length;
                if (remoteSize > 0 && existingLen == remoteSize)
                {
                    PathRegistry.Claim(item.Id, item.FullPath);
                    item.TotalSize = remoteSize;
                    item.Downloaded = remoteSize;
                    FinalizeFile(item.FullPath);
                    return;
                }
                if (existingLen > 0 && (remoteSize < 0 || existingLen < remoteSize))
                {
                    resumeOffset = existingLen;
                    PathRegistry.Claim(item.Id, item.FullPath);
                    reservedPath = item.FullPath;
                }
                else
                {
                    MediaDownloader.TryDelete(item.TempPath);
                    item.ActiveTempPath = "";
                    reservedPath = PathRegistry.Reserve(item.Id, item.FullPath);
                    item.FileName = Path.GetFileName(reservedPath);
                }
            }
            else
            {
                if (hasExistingTemp)
                {
                    MediaDownloader.TryDelete(item.TempPath);
                    item.ActiveTempPath = "";
                }
                reservedPath = PathRegistry.Reserve(item.Id, item.FullPath);
                item.FileName = Path.GetFileName(reservedPath);
                item.ActiveTempPath = "";
            }

            if (remoteSize >= 0) item.TotalSize = remoteSize;
            item.Downloaded = resumeOffset;

            using var dataTcp = await session.OpenPassiveSocketAsync(ct).ConfigureAwait(false);
            if (resumeOffset > 0)
            {
                var rest = await session.SendCommandAsync("REST " + resumeOffset.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                if (rest.Code == 350)
                {
                    item.Resumable = true;
                }
                else
                {
                    resumeOffset = 0;
                    item.Resumable = false;
                    item.Downloaded = 0;
                    MediaDownloader.TryDelete(item.TempPath);
                    item.ActiveTempPath = "";
                    reservedPath = PathRegistry.Reserve(item.Id, item.FullPath);
                    item.FileName = Path.GetFileName(reservedPath);
                    item.ActiveTempPath = "";
                }
            }
            var retrReply = await session.SendCommandAsync("RETR " + remotePath, ct).ConfigureAwait(false);
            if (retrReply.Code is not (125 or 150))
            {
                FailKind kind = retrReply.Code is 421 or 425 or 426 or 450 or 451 ? FailKind.Retryable : FailKind.General;
                throw new DownloadException(Loc.T("core.ftp.err_status", retrReply.Code, retrReply.Text), kind) { StatusCode = retrReply.Code };
            }

            await using Stream dataStream = await session.WrapDataStreamAsync(dataTcp, ct).ConfigureAwait(false);
            item.Status = DownloadStatus.Downloading;
            item.ActiveConnections = 1;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            bool sizeExceeded = false;
            try
            {
                var mode = resumeOffset > 0 ? FileMode.Append : FileMode.Create;
                await using var fs = new FileStream(item.TempPath, mode, FileAccess.Write, FileShare.Read);
                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(HttpDownloader.ReadTimeout);
                    int n;
                    try
                    {
                        n = await dataStream.ReadAsync(buffer.AsMemory(0, BufferSize), readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new DownloadException(Loc.T("core.http.err_timeout"), FailKind.Retryable);
                    }
                    if (n == 0) break;

                    if ((remoteSize >= 0 && item.Downloaded + n > remoteSize)
                        || item.Downloaded + n > HttpDownloader.MaxDownloadSize
                        || (remoteSize < 0 && item.Downloaded + n > HttpDownloader.MaxUnknownSize))
                    {
                        sizeExceeded = true;
                        throw new DownloadException(Loc.T(remoteSize >= 0 && item.Downloaded + n <= HttpDownloader.MaxDownloadSize
                            ? "core.http.err_stream_exceeded"
                            : "core.http.err_file_too_large"));
                    }

                    await limiter.WaitAsync(n, ct).ConfigureAwait(false);
                    await item.FileLimiter.WaitAsync(n, ct).ConfigureAwait(false);
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    item.AddDownloaded(n);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                item.ActiveConnections = 0;
                if (sizeExceeded) MediaDownloader.TryDelete(item.TempPath);
            }

            dataTcp.Close();
            var doneReply = await session.ReadReplyAsync(ct).ConfigureAwait(false);
            if (doneReply.Code is not (226 or 250))
                throw new DownloadException(Loc.T("core.ftp.err_status", doneReply.Code, doneReply.Text), FailKind.Retryable);

            try { await session.SendCommandAsync("QUIT", ct).ConfigureAwait(false); }
            catch { /* ignore quit error after successful transfer */ }

            if (remoteSize > 0 && item.Downloaded != remoteSize)
                throw new DownloadException(Loc.T("core.http.err_disconnected"), FailKind.Retryable);

            if (item.TotalSize <= 0)
                item.TotalSize = item.Downloaded;

            FinalizeFile(reservedPath);
        }
        finally
        {
            PathRegistry.Release(item.Id);
        }
    }

    void FinalizeFile(string targetPath)
    {
        item.Status = DownloadStatus.Merging;
        MarkOfTheWeb.Write(item.TempPath, Log.StripUserInfo(item.Url), Log.StripUserInfo(item.Referrer));
        var finalTarget = targetPath;
        if (!File.Exists(finalTarget) || !string.Equals(Path.GetFullPath(item.TempPath), Path.GetFullPath(finalTarget), StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(finalTarget))
                finalTarget = PathRegistry.Reserve(item.Id, item.FullPath);
            File.Move(item.TempPath, finalTarget);
            item.FileName = Path.GetFileName(finalTarget);
        }
        item.ActiveTempPath = "";
        MarkOfTheWeb.Write(item.FullPath, Log.StripUserInfo(item.Url), Log.StripUserInfo(item.Referrer));
    }

    static string GetRemotePath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (string.IsNullOrEmpty(path)) path = "/";
        return path.Replace("\r", "").Replace("\n", "");
    }

    static (string User, string Pass) ResolveCredentials(DownloadItem item, Uri uri)
    {
        if (!string.IsNullOrEmpty(item.Login))
            return (SanitizeLine(item.Login), SanitizeLine(item.Password));

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            int colon = uri.UserInfo.IndexOf(':');
            if (colon >= 0)
                return (SanitizeLine(Uri.UnescapeDataString(uri.UserInfo[..colon])), SanitizeLine(Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..])));
            return (SanitizeLine(Uri.UnescapeDataString(uri.UserInfo)), "");
        }

        if (HttpDownloader.Options.LoginFor(uri) is { } saved && !string.IsNullOrEmpty(saved.User))
            return (SanitizeLine(saved.User), SanitizeLine(saved.Password));

        return ("anonymous", "anonymous@example.com");
    }

    static string SanitizeLine(string s) => s.Replace("\r", "").Replace("\n", "");

    internal static void EnsureFtpsDataProtection((int Code, string Text) protReply)
    {
        if (protReply.Code != 200)
            throw new DownloadException(Loc.T("core.ftp.err_status", protReply.Code, protReply.Text)) { StatusCode = protReply.Code };
    }

    static async Task<FtpControlSession> ConnectAndLoginAsync(DownloadItem item, Uri uri, CancellationToken ct)
    {
        bool isFtps = string.Equals(uri.Scheme, "ftps", StringComparison.OrdinalIgnoreCase);
        bool implicitTls = isFtps && uri.Port == 990;
        int port = uri.IsDefaultPort ? (implicitTls ? 990 : 21) : uri.Port;

        var tcp = new TcpClient();
        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(HttpDownloader.HeaderTimeout);
                await tcp.ConnectAsync(uri.DnsSafeHost, port, connectCts.Token).ConfigureAwait(false);
            }

            var session = new FtpControlSession(tcp, uri.DnsSafeHost, isFtps);
            if (implicitTls)
                await session.UpgradeControlToTlsAsync(ct).ConfigureAwait(false);

            var greet = await session.ReadReplyAsync(ct).ConfigureAwait(false);
            if (greet.Code != 220)
                throw new DownloadException(Loc.T("core.ftp.err_status", greet.Code, greet.Text));

            if (isFtps && !implicitTls)
            {
                var auth = await session.SendCommandAsync("AUTH TLS", ct).ConfigureAwait(false);
                if (auth.Code != 234)
                    throw new DownloadException(Loc.T("core.ftp.err_status", auth.Code, auth.Text));
                await session.UpgradeControlToTlsAsync(ct).ConfigureAwait(false);
            }

            var (user, pass) = ResolveCredentials(item, uri);
            var userReply = await session.SendCommandAsync("USER " + user, ct).ConfigureAwait(false);
            if (userReply.Code == 331)
            {
                var passReply = await session.SendCommandAsync("PASS " + pass, ct).ConfigureAwait(false);
                if (passReply.Code == 530)
                    throw new DownloadException(Loc.T("core.ftp.err_auth"), FailKind.Expired) { StatusCode = 530 };
                if (passReply.Code is not (230 or 202))
                    throw new DownloadException(Loc.T("core.ftp.err_status", passReply.Code, passReply.Text)) { StatusCode = passReply.Code };
            }
            else if (userReply.Code == 530)
            {
                throw new DownloadException(Loc.T("core.ftp.err_auth"), FailKind.Expired) { StatusCode = 530 };
            }
            else if (userReply.Code != 230)
            {
                throw new DownloadException(Loc.T("core.ftp.err_status", userReply.Code, userReply.Text)) { StatusCode = userReply.Code };
            }

            if (isFtps)
            {
                await session.SendCommandAsync("PBSZ 0", ct).ConfigureAwait(false);
                var prot = await session.SendCommandAsync("PROT P", ct).ConfigureAwait(false);
                EnsureFtpsDataProtection(prot);
                session.ProtectDataChannel = true;
            }

            await session.SendCommandAsync("OPTS UTF8 ON", ct).ConfigureAwait(false);
            var typeReply = await session.SendCommandAsync("TYPE I", ct).ConfigureAwait(false);
            if (typeReply.Code != 200)
                throw new DownloadException(Loc.T("core.ftp.err_status", typeReply.Code, typeReply.Text));

            return session;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new DownloadException(Loc.T("core.http.err_timeout"), FailKind.Retryable);
        }
        catch (SocketException)
        {
            tcp.Dispose();
            throw new DownloadException(Loc.T("core.err.network"), FailKind.Retryable);
        }
        catch (System.Security.Authentication.AuthenticationException)
        {
            tcp.Dispose();
            throw new DownloadException(Loc.T("core.http.err_ssl"));
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    sealed class FtpControlSession : IAsyncDisposable
    {
        readonly TcpClient _tcp;
        readonly string _host;
        readonly bool _isFtps;
        Stream _stream;
        StreamReader _reader;

        public bool ProtectDataChannel { get; set; }

        public FtpControlSession(TcpClient tcp, string host, bool isFtps)
        {
            _tcp = tcp;
            _host = host;
            _isFtps = isFtps;
            _stream = tcp.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        }

        public async Task UpgradeControlToTlsAsync(CancellationToken ct)
        {
            var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _host }, ct).ConfigureAwait(false);
            _stream = ssl;
            _reader = new StreamReader(_stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        }

        public async Task<Stream> WrapDataStreamAsync(TcpClient dataTcp, CancellationToken ct)
        {
            Stream net = dataTcp.GetStream();
            if (!_isFtps || !ProtectDataChannel)
                return net;
            var ssl = new SslStream(net, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _host }, ct).ConfigureAwait(false);
            return ssl;
        }

        public async Task<(int Code, string Text)> SendCommandAsync(string command, CancellationToken ct)
        {
            var line = Encoding.UTF8.GetBytes(command + "\r\n");
            using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            writeCts.CancelAfter(HttpDownloader.HeaderTimeout);
            await _stream.WriteAsync(line, writeCts.Token).ConfigureAwait(false);
            await _stream.FlushAsync(writeCts.Token).ConfigureAwait(false);
            return await ReadReplyAsync(ct).ConfigureAwait(false);
        }

        public async Task<(int Code, string Text)> ReadReplyAsync(CancellationToken ct)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(HttpDownloader.HeaderTimeout);

            string? first;
            try { first = await _reader.ReadLineAsync(readCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DownloadException(Loc.T("core.http.err_timeout"), FailKind.Retryable);
            }
            if (first is null || first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
                throw new DownloadException(Loc.T("core.http.err_disconnected"), FailKind.Retryable);

            if (first.Length > 3 && first[3] == '-')
            {
                var sb = new StringBuilder(first.Length > 4 ? first[4..] : "");
                string endPrefix = first[..3] + " ";
                while (true)
                {
                    var next = await _reader.ReadLineAsync(readCts.Token).ConfigureAwait(false)
                               ?? throw new DownloadException(Loc.T("core.http.err_disconnected"), FailKind.Retryable);
                    if (next.StartsWith(endPrefix, StringComparison.Ordinal))
                    {
                        if (next.Length > 4)
                        {
                            if (sb.Length > 0) sb.Append('\n');
                            sb.Append(next[4..]);
                        }
                        break;
                    }
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(next);
                }
                return (code, sb.ToString());
            }

            return (code, first.Length > 4 ? first[4..] : "");
        }

        public async Task<TcpClient> OpenPassiveSocketAsync(CancellationToken ct)
        {
            var controlIp = (_tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;

            var epsv = await SendCommandAsync("EPSV", ct).ConfigureAwait(false);
            if (epsv.Code == 229)
            {
                var m = Regex.Match(epsv.Text, @"\(\|\|\|(\d+)\|\)");
                if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535)
                {
                    return await ConnectDataAsync(controlIp, _host, port, ct).ConfigureAwait(false);
                }
            }

            var pasv = await SendCommandAsync("PASV", ct).ConfigureAwait(false);
            if (pasv.Code == 227)
            {
                var m = Regex.Match(pasv.Text, @"(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)");
                if (m.Success)
                {
                    int p1 = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
                    int p2 = int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture);
                    int port = (p1 << 8) + p2;
                    if (port is > 0 and <= 65535)
                    {
                        string ipStr = $"{m.Groups[1].Value}.{m.Groups[2].Value}.{m.Groups[3].Value}.{m.Groups[4].Value}";
                        // Prevent FTP bounce SSRF: always connect to the control peer's address when PASV advertises a different or unspecified host.
                        IPAddress? targetIp = controlIp;
                        if (targetIp is null && IPAddress.TryParse(ipStr, out var parsedIp) && !parsedIp.Equals(IPAddress.Any))
                            targetIp = parsedIp;
                        return await ConnectDataAsync(targetIp, _host, port, ct).ConfigureAwait(false);
                    }
                }
            }

            throw new DownloadException(Loc.T("core.ftp.err_pasv"));
        }

        static async Task<TcpClient> ConnectDataAsync(IPAddress? ip, string fallbackHost, int port, CancellationToken ct)
        {
            var data = new TcpClient();
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(HttpDownloader.HeaderTimeout);
                if (ip != null)
                    await data.ConnectAsync(ip, port, connectCts.Token).ConfigureAwait(false);
                else
                    await data.ConnectAsync(fallbackHost, port, connectCts.Token).ConfigureAwait(false);
                return data;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                data.Dispose();
                throw new DownloadException(Loc.T("core.http.err_timeout"), FailKind.Retryable);
            }
            catch (SocketException)
            {
                data.Dispose();
                throw new DownloadException(Loc.T("core.err.network"), FailKind.Retryable);
            }
            catch
            {
                data.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            try { _reader.Dispose(); } catch { /* ignore reader dispose */ }
            try { _stream.Dispose(); } catch { /* ignore stream dispose */ }
            try { _tcp.Dispose(); } catch { /* ignore socket dispose */ }
            return ValueTask.CompletedTask;
        }
    }
}
