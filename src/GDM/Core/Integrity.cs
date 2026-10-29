using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Golan.Gdm.Core;

public sealed record HashSpec(HashAlgorithmName Algorithm, string Hex, byte[] Bytes)
{
    public static HashSpec? Parse(string? input)
        => TryParse(input, out var spec) ? spec : null;

    public static bool TryParse(string? input, out HashSpec? spec)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();

        HashAlgorithmName? explicitAlgo = null;
        int colonIdx = s.IndexOf(':');
        if (colonIdx >= 0)
        {
            var prefix = s[..colonIdx].Trim().ToLowerInvariant();
            explicitAlgo = prefix switch
            {
                "md5" => HashAlgorithmName.MD5,
                "sha1" or "sha-1" => HashAlgorithmName.SHA1,
                "sha256" or "sha-256" => HashAlgorithmName.SHA256,
                "sha512" or "sha-512" => HashAlgorithmName.SHA512,
                _ => null,
            };
            if (explicitAlgo == null) return false;
            s = s[(colonIdx + 1)..].Trim();
        }

        if (s.Length is not (32 or 40 or 64 or 128)) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }

        var detectedAlgo = s.Length switch
        {
            32 => HashAlgorithmName.MD5,
            40 => HashAlgorithmName.SHA1,
            64 => HashAlgorithmName.SHA256,
            128 => HashAlgorithmName.SHA512,
            _ => default,
        };

        if (explicitAlgo != null && explicitAlgo.Value != detectedAlgo)
            return false;

        byte[] bytes;
        try { bytes = Convert.FromHexString(s); }
        catch { return false; }

        spec = new HashSpec(detectedAlgo, s.ToLowerInvariant(), bytes);
        return true;
    }
}

public static class Integrity
{
    public const int BufferSize = 1024 * 1024; // 1 MB streaming buffer
    public const int MaxSumsBytes = 64 * 1024; // 64 KB limit for remote SUMS files

    public static readonly HashAlgorithmName[] AllAlgorithms =
    [
        HashAlgorithmName.MD5,
        HashAlgorithmName.SHA1,
        HashAlgorithmName.SHA256,
        HashAlgorithmName.SHA512,
    ];

    static readonly HttpClient SumsHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>Streams <paramref name="path"/> once on a background thread with a 1 MB buffer, computing all requested algorithms simultaneously.</summary>
    public static Task<Dictionary<HashAlgorithmName, string>> ComputeAsync(
        string path,
        IEnumerable<HashAlgorithmName> algos,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(algos);
        var algoList = algos.Distinct().ToArray();

        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var result = new Dictionary<HashAlgorithmName, string>(algoList.Length);
            if (algoList.Length == 0)
            {
                progress?.Report(1.0);
                return result;
            }

            var hashers = new (HashAlgorithmName Name, IncrementalHash Hasher)[algoList.Length];
            try
            {
                for (int i = 0; i < algoList.Length; i++)
                    hashers[i] = (algoList[i], IncrementalHash.CreateHash(algoList[i]));

                await using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                long totalLength = fs.Length;
                if (totalLength <= 0)
                {
                    progress?.Report(1.0);
                }
                else
                {
                    var buffer = new byte[BufferSize];
                    long totalRead = 0;
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int n = await fs.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false);
                        if (n <= 0) break;

                        for (int i = 0; i < hashers.Length; i++)
                            hashers[i].Hasher.AppendData(buffer, 0, n);

                        totalRead += n;
                        progress?.Report(Math.Clamp((double)totalRead / totalLength, 0.0, 1.0));
                    }
                    if (totalRead < totalLength)
                        progress?.Report(1.0);
                }

                for (int i = 0; i < hashers.Length; i++)
                {
                    var digest = hashers[i].Hasher.GetHashAndReset();
                    result[hashers[i].Name] = Convert.ToHexString(digest).ToLowerInvariant();
                }

                return result;
            }
            finally
            {
                for (int i = 0; i < hashers.Length; i++)
                    hashers[i].Hasher?.Dispose();
            }
        }, ct);
    }

    /// <summary>Constant-time byte comparison using <see cref="CryptographicOperations.FixedTimeEquals"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> computed, ReadOnlySpan<byte> expected)
        => computed.Length > 0 && computed.Length == expected.Length && CryptographicOperations.FixedTimeEquals(computed, expected);

    public static bool VerifyHex(string? computedHex, string? expectedInput)
    {
        if (!HashSpec.TryParse(computedHex, out var comp) || comp == null) return false;
        if (!HashSpec.TryParse(expectedInput, out var exp) || exp == null) return false;
        if (comp.Algorithm != exp.Algorithm) return false;
        return Verify(comp.Bytes, exp.Bytes);
    }

    /// <summary>Verifies <paramref name="item"/> after completion; never deletes the file on mismatch.</summary>
    public static async Task<bool> VerifyItemAsync(
        DownloadItem item,
        AppSettings? settings = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.ExpectedHash) && settings?.AutoDiscoverHash == true && !string.IsNullOrWhiteSpace(item.Url))
        {
            var discovered = await DiscoverHashAsync(item.Url, item.FileName, ct: ct).ConfigureAwait(false);
            if (discovered != null)
                item.ExpectedHash = $"{discovered.Algorithm.Name?.ToLowerInvariant()}:{discovered.Hex}";
        }

        if (string.IsNullOrWhiteSpace(item.ExpectedHash))
        {
            item.HashState = HashVerifyState.None;
            return true;
        }

        if (!HashSpec.TryParse(item.ExpectedHash, out var spec) || spec == null)
        {
            item.ErrorMessage = Loc.T("core.http.err_checksum_format");
            item.HashState = HashVerifyState.Error;
            return false;
        }

        if (!File.Exists(item.FullPath))
        {
            item.HashState = HashVerifyState.Error;
            return false;
        }

        item.HashState = HashVerifyState.Verifying;
        try
        {
            var map = await ComputeAsync(item.FullPath, [spec.Algorithm], progress, ct).ConfigureAwait(false);
            if (!map.TryGetValue(spec.Algorithm, out var actualHex) || !HashSpec.TryParse(actualHex, out var actualSpec) || actualSpec == null)
            {
                item.HashState = HashVerifyState.Error;
                return false;
            }

            item.CalculatedHash = actualSpec.Hex;
            bool ok = Verify(actualSpec.Bytes, spec.Bytes);
            if (ok)
            {
                item.HashState = HashVerifyState.Verified;
                item.LastResult = Loc.T($"hash.status.verified");
                return true;
            }
            else
            {
                item.HashState = HashVerifyState.Mismatch;
                item.LastResult = Loc.T($"hash.status.mismatch");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            item.HashState = HashVerifyState.None;
            throw;
        }
        catch (Exception ex)
        {
            item.ErrorMessage = ex.Message;
            item.HashState = HashVerifyState.Error;
            return false;
        }
    }

    static readonly Regex BsdRegex = new(
        @"^(?<algo>MD5|SHA1|SHA-1|SHA256|SHA-256|SHA512|SHA-512)\s*\((?<file>[^)]+)\)\s*=\s*(?<hash>[0-9a-fA-F]{32,128})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    /// <summary>Parses GNU ("hash  name", "hash *name"), BSD ("SHA256 (name) = hash"), or single-digest checksum text for <paramref name="targetFileName"/>.</summary>
    public static HashSpec? ParseSums(
        string? content,
        string targetFileName,
        HashAlgorithmName? defaultAlgo = null,
        bool allowBareSingleHash = true)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(targetFileName)) return null;
        if (IsUnsafeSumsFileName(targetFileName)) return null;

        var cleanTarget = Path.GetFileName(targetFileName.Trim());
        if (string.IsNullOrEmpty(cleanTarget)) return null;

        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        HashSpec? singleDigestCandidate = null;
        int nonCommentLines = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            nonCommentLines++;

            // 1. BSD format: SHA256 (filename) = <hex>
            var bsdMatch = BsdRegex.Match(line);
            if (bsdMatch.Success)
            {
                var filePart = bsdMatch.Groups["file"].Value.Trim();
                var algoPart = bsdMatch.Groups["algo"].Value.Trim();
                var hashPart = bsdMatch.Groups["hash"].Value.Trim();
                if (!IsUnsafeSumsFileName(filePart) && string.Equals(filePart, cleanTarget, StringComparison.OrdinalIgnoreCase))
                {
                    if (HashSpec.TryParse($"{algoPart}:{hashPart}", out var bsdSpec) && bsdSpec != null)
                        return bsdSpec;
                }
                continue;
            }

            // 2. GNU format: <hex>  <filename> or <hex> *<filename>
            int spaceIdx = line.IndexOfAny([' ', '\t']);
            if (spaceIdx > 0)
            {
                var hashPart = line[..spaceIdx].Trim();
                var rest = line[(spaceIdx + 1)..].Trim();
                if (rest.StartsWith('*') || rest.StartsWith(' '))
                    rest = rest[1..].Trim();

                if (!IsUnsafeSumsFileName(rest) && string.Equals(rest, cleanTarget, StringComparison.OrdinalIgnoreCase))
                {
                    if (HashSpec.TryParse(hashPart, out var gnuSpec) && gnuSpec != null)
                        return defaultAlgo == null || gnuSpec.Algorithm == defaultAlgo.Value ? gnuSpec : null;
                }
                continue;
            }

            // 3. Single hex token line (e.g. file.ext.sha256 containing only the digest)
            if (allowBareSingleHash && HashSpec.TryParse(line, out var bareSpec) && bareSpec != null)
            {
                if (defaultAlgo == null || bareSpec.Algorithm == defaultAlgo.Value)
                    singleDigestCandidate = bareSpec;
            }
        }

        return allowBareSingleHash && nonCommentLines == 1 ? singleDigestCandidate : null;
    }

    static bool IsUnsafeSumsFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.Contains('/') || name.Contains('\\') || name.Contains(':') || name.Contains("..", StringComparison.Ordinal))
            return true;
        return false;
    }

    /// <summary>Probes &lt;file&gt;.sha256, &lt;file&gt;.sha256sum, SHA256SUMS, and sha256sums.txt in the same remote folder (same host only, &lt;= 64 KB, 10 s timeout).</summary>
    public static async Task<HashSpec?> DiscoverHashAsync(
        string fileUrl,
        string? fileName = null,
        HttpClient? client = null,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var baseUri)) return null;
        if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) return null;

        var targetName = !string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(fileName.Trim())
            : Path.GetFileName(Uri.UnescapeDataString(baseUri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(targetName) || IsUnsafeSumsFileName(targetName)) return null;

        var path = baseUri.AbsolutePath;
        int lastSlash = path.LastIndexOf('/');
        var dirPath = lastSlash >= 0 ? path[..(lastSlash + 1)] : "/";
        var escapedFile = Uri.EscapeDataString(targetName);

        (string Path, bool AllowBare)[] candidates =
        [
            ($"{dirPath}{escapedFile}.sha256", true),
            ($"{dirPath}{escapedFile}.sha256sum", true),
            ($"{dirPath}SHA256SUMS", false),
            ($"{dirPath}sha256sums.txt", false),
        ];

        var http = client ?? SumsHttpClient;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        foreach (var (candidatePath, allowBare) in candidates)
        {
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            if (timeoutCts.IsCancellationRequested) break;
            var candidateUri = new UriBuilder(baseUri) { Path = candidatePath, Query = "", Fragment = "" }.Uri;
            var content = await FetchSameHostTextAsync(http, baseUri, candidateUri, ct, timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content)) continue;

            var parsed = ParseSums(content, targetName, defaultAlgo: HashAlgorithmName.SHA256, allowBareSingleHash: allowBare);
            if (parsed != null) return parsed;
        }

        return null;
    }

    static bool IsSameOriginAllowed(Uri originUri, Uri targetUri)
        => string.Equals(targetUri.Host, originUri.Host, StringComparison.OrdinalIgnoreCase) &&
           targetUri.Port == originUri.Port &&
           !(originUri.Scheme == Uri.UriSchemeHttps && targetUri.Scheme != Uri.UriSchemeHttps);

    static async Task<string?> FetchSameHostTextAsync(HttpClient http, Uri originUri, Uri requestUri, CancellationToken callerCt, CancellationToken token)
    {
        Uri current = requestUri;
        for (int hop = 0; hop <= 3; hop++)
        {
            if (!IsSameOriginAllowed(originUri, current))
                return null;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, current);
                req.Headers.TryAddWithoutValidation("User-Agent", HttpDownloader.DefaultUserAgent);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (resp.RequestMessage?.RequestUri is Uri finalUri && !IsSameOriginAllowed(originUri, finalUri))
                    return null;

                if ((int)resp.StatusCode is >= 300 and < 400)
                {
                    var loc = resp.Headers.Location;
                    if (loc == null) return null;
                    var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                    if (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) return null;
                    current = next;
                    continue;
                }

                if (resp.StatusCode != HttpStatusCode.OK) return null;
                if (resp.Content.Headers.ContentLength is > MaxSumsBytes) return null;

                await using var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var buf = new byte[MaxSumsBytes + 1];
                int total = 0;
                while (total < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(total, buf.Length - total), token).ConfigureAwait(false);
                    if (n <= 0) break;
                    total += n;
                }
                if (total > MaxSumsBytes) return null;

                return Encoding.UTF8.GetString(buf, 0, total);
            }
            catch (OperationCanceledException) when (callerCt.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }
        return null;
    }
}
