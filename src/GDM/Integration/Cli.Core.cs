using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Golan.Gdm.Core;

namespace Golan.Gdm.Integration;

public sealed record CliCommand(
    string Verb,
    string? Url = null,
    string? FilePath = null,
    string? QueueName = null,
    string? Folder = null,
    string? FileName = null,
    string? Referer = null,
    bool? StartNow = null,
    string? Quality = null,
    string? ExpectedHash = null,
    bool JsonOutput = false)
{
    public JsonObject ToPipeMessage(IReadOnlyList<string>? resolvedFileUrls = null)
    {
        var msg = new JsonObject
        {
            ["action"] = "cli",
            ["verb"] = Verb,
        };

        if (!string.IsNullOrEmpty(Url)) msg["url"] = Url;
        if (!string.IsNullOrEmpty(FilePath)) msg["file"] = FilePath;
        if (!string.IsNullOrEmpty(QueueName)) msg["queue"] = QueueName;
        if (!string.IsNullOrEmpty(Folder)) msg["folder"] = Folder;
        if (!string.IsNullOrEmpty(FileName)) msg["name"] = FileName;
        if (!string.IsNullOrEmpty(Referer)) msg["referer"] = Referer;
        if (StartNow.HasValue) msg["start"] = StartNow.Value;
        if (!string.IsNullOrEmpty(Quality)) msg["quality"] = Quality;
        if (!string.IsNullOrEmpty(ExpectedHash)) msg["hash"] = ExpectedHash;
        if (JsonOutput) msg["json"] = true;

        if (resolvedFileUrls != null)
        {
            var arr = new JsonArray();
            foreach (var u in resolvedFileUrls) arr.Add(u);
            msg["urls"] = arr;
        }

        return msg;
    }
}

public sealed record CliParseResult(
    bool Ok,
    CliCommand? Command,
    string? ErrorMessage,
    int ExitCode = 0);

public sealed record CliAddRequest(
    string Url,
    string? QueueName = null,
    string? Folder = null,
    string? FileName = null,
    string? Referer = null,
    bool? StartNow = null,
    string? Quality = null,
    string? ExpectedHash = null);

public sealed record CliListEntry(
    Guid Id,
    string FileName,
    string Url,
    string Status,
    long Downloaded,
    long TotalSize,
    string? QueueName = null);

public interface ICliTarget
{
    Task<(bool Ok, string Message)> AddDownloadAsync(CliAddRequest request);
    Task<(bool Ok, string Message)> StartQueueAsync(string queueName);
    Task<(bool Ok, string Message)> StopQueueAsync(string queueName);
    Task<(bool Ok, string Message)> PauseAllAsync();
    Task<IReadOnlyList<CliListEntry>> ListDownloadsAsync();
}

public static partial class CliParser
{
    static readonly HashSet<string> KnownVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "add", "add-file", "start-queue", "stop-queue", "pause-all", "list",
        "--help", "-h", "/?", "help",
    };

    public const long MaxUrlListFileBytes = 4L * 1024 * 1024;
    public const int MaxUrlsPerBatch = 1000;

    static readonly Regex HashSpecRegex = new(
        @"^(?:(md5:[0-9a-fA-F]{32})|(sha-?1:[0-9a-fA-F]{40})|(sha-?256:[0-9a-fA-F]{64})|(sha-?512:[0-9a-fA-F]{128})|[0-9a-fA-F]{32}|[0-9a-fA-F]{40}|[0-9a-fA-F]{64}|[0-9a-fA-F]{128})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    static readonly Regex QualitySpecRegex = new(
        @"^(?:best|mp3|m4a|audio|\d{2,4}p?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(250));

    public static bool IsCliInvocation(string[]? args)
    {
        if (args == null || args.Length == 0) return false;
        var first = args[0].Trim();
        if (string.IsNullOrEmpty(first)) return false;

        if (string.Equals(first, "--background", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "--native-host", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "--install", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "--uninstall", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "--apply-update", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (first.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            || first.Trim('"').EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length == 1 && IsValidDownloadUrl(first))
            return false;

        return KnownVerbs.Contains(first);
    }

    public static bool IsValidDownloadUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        if (trimmed.Any(char.IsControl)) return false;

        if (trimmed.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return trimmed.Contains("xt=urn:", StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("http" or "https" or "ftp"))
            return false;

        return !string.IsNullOrWhiteSpace(uri.Host);
    }

    public static CliParseResult Parse(string[]? args)
    {
        if (args == null || args.Length == 0)
            return Fail("missing command");

        var verb = args[0].Trim().ToLowerInvariant();
        if (verb is "--help" or "-h" or "/?" or "help")
            return new CliParseResult(true, new CliCommand("help"), null, 0);

        switch (verb)
        {
            case "add":
                return ParseAdd(args);

            case "add-file":
                if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
                    return Fail("add-file requires <list.txt>");
                return new CliParseResult(true, new CliCommand("add-file", FilePath: args[1].Trim()), null, 0);

            case "start-queue":
                if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
                    return Fail("start-queue requires <name>");
                return new CliParseResult(true, new CliCommand("start-queue", QueueName: args[1].Trim()), null, 0);

            case "stop-queue":
                if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith('-'))
                    return Fail("stop-queue requires <name>");
                return new CliParseResult(true, new CliCommand("stop-queue", QueueName: args[1].Trim()), null, 0);

            case "pause-all":
                if (args.Length != 1)
                    return Fail("pause-all takes no arguments");
                return new CliParseResult(true, new CliCommand("pause-all"), null, 0);

            case "list":
                if (args.Length == 1)
                    return new CliParseResult(true, new CliCommand("list", JsonOutput: false), null, 0);
                if (args.Length == 2 && string.Equals(args[1].Trim(), "--json", StringComparison.OrdinalIgnoreCase))
                    return new CliParseResult(true, new CliCommand("list", JsonOutput: true), null, 0);
                return Fail("list only accepts [--json]");

            default:
                return Fail($"unknown command '{args[0]}'");
        }
    }

    static CliParseResult ParseAdd(string[] args)
    {
        if (args.Length < 2)
            return Fail("add requires <url>");

        var url = args[1].Trim();
        if (!IsValidDownloadUrl(url))
            return new CliParseResult(false, null, Loc.T("cli.err.invalid_url", url), 2);

        string? queue = null;
        string? folder = null;
        string? name = null;
        string? referer = null;
        bool sawStart = false;
        bool sawLater = false;
        string? quality = null;
        string? hash = null;

        for (int i = 2; i < args.Length; i++)
        {
            var flag = args[i].Trim();
            switch (flag.ToLowerInvariant())
            {
                case "--queue":
                    if (!TryReadFlagValue(args, ref i, out queue))
                        return Fail("--queue requires a queue name");
                    break;

                case "--folder":
                    if (!TryReadFlagValue(args, ref i, out folder) || !IsSafeFolderArg(folder))
                        return Fail("--folder requires a valid directory path");
                    break;

                case "--name":
                    if (!TryReadFlagValue(args, ref i, out name) || !IsSafeFileNameArg(name))
                        return Fail("--name requires a valid file name");
                    break;

                case "--referer":
                    if (!TryReadFlagValue(args, ref i, out referer) || !FileNames.IsHttpUrl(referer))
                        return Fail("--referer requires a valid HTTP/HTTPS URL");
                    break;

                case "--start":
                    sawStart = true;
                    if (sawLater) return Fail("--start and --later cannot be used together");
                    break;

                case "--later":
                    sawLater = true;
                    if (sawStart) return Fail("--start and --later cannot be used together");
                    break;

                case "--quality":
                    if (!TryReadFlagValue(args, ref i, out quality) || !IsValidQualitySpec(quality))
                        return Fail("--quality requires a valid quality value");
                    break;

                case "--hash":
                    if (!TryReadFlagValue(args, ref i, out hash) || !IsValidHashSpec(hash))
                        return Fail("--hash requires a valid hash (e.g. sha256:<64 hex>)");
                    break;

                default:
                    return Fail($"unknown option '{flag}'");
            }
        }

        bool? startNow = sawStart ? true : sawLater ? false : null;
        var cmd = new CliCommand(
            "add",
            Url: url,
            QueueName: queue,
            Folder: folder,
            FileName: name,
            Referer: referer,
            StartNow: startNow,
            Quality: quality,
            ExpectedHash: hash);

        return new CliParseResult(true, cmd, null, 0);
    }

    static bool TryReadFlagValue(string[] args, ref int index, out string value)
    {
        value = "";
        if (index + 1 >= args.Length) return false;
        var next = args[index + 1].Trim();
        if (string.IsNullOrWhiteSpace(next) || next.StartsWith("--", StringComparison.Ordinal))
            return false;
        index++;
        value = next;
        return true;
    }

    public static bool IsSafeFolderArg(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var trimmed = folder.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return false;
        if (trimmed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;
        if (trimmed.Contains('*') || trimmed.Contains('?') || trimmed.Any(char.IsControl))
            return false;
        int colonIdx = trimmed.IndexOf(':');
        if (colonIdx >= 0 && (colonIdx != 1 || !char.IsAsciiLetter(trimmed[0]) || trimmed.IndexOf(':', colonIdx + 1) >= 0))
            return false;
        var segments = trimmed.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return !segments.Any(s => s == "..");
    }

    public static bool IsSafeFileNameArg(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains("..", StringComparison.Ordinal) || trimmed.Contains(':'))
            return false;
        return trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    public static bool IsValidQualitySpec(string? quality)
        => !string.IsNullOrWhiteSpace(quality) && QualitySpecRegex.IsMatch(quality.Trim());

    public static bool IsValidHashSpec(string? hash)
        => !string.IsNullOrWhiteSpace(hash) && HashSpecRegex.IsMatch(hash.Trim());

    static CliParseResult Fail(string reason)
        => new(false, null, Loc.T("cli.err.bad_args", reason), 2);

    public static List<string> ParseUrlList(string content, out string? invalidLine)
    {
        invalidLine = null;
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(content)) return list;

        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (!IsValidDownloadUrl(line))
            {
                invalidLine ??= line;
                continue;
            }
            list.Add(line);
        }
        return list;
    }

    public static string GetHelpText() => Loc.T("cli.help");
}

public static partial class CliActions
{
    public static async Task<JsonObject> HandleAsync(JsonObject msg, ICliTarget target)
    {
        ArgumentNullException.ThrowIfNull(msg);
        ArgumentNullException.ThrowIfNull(target);

        var action = Str(msg, "action");
        if (!string.Equals(action, "cli", StringComparison.OrdinalIgnoreCase))
            return FailJson("Invalid action");

        var verb = Str(msg, "verb").ToLowerInvariant();
        switch (verb)
        {
            case "add":
            {
                var url = Str(msg, "url");
                if (!CliParser.IsValidDownloadUrl(url))
                    return FailJson(Loc.T("cli.err.invalid_url", url));

                var folder = NullIfEmpty(Str(msg, "folder"));
                if (folder != null && !CliParser.IsSafeFolderArg(folder))
                    return FailJson(Loc.T("cli.err.bad_args", "--folder"));

                var name = NullIfEmpty(Str(msg, "name"));
                if (name != null && !CliParser.IsSafeFileNameArg(name))
                    return FailJson(Loc.T("cli.err.bad_args", "--name"));

                var referer = NullIfEmpty(Str(msg, "referer"));
                if (referer != null && !FileNames.IsHttpUrl(referer))
                    return FailJson(Loc.T("cli.err.bad_args", "--referer"));

                var quality = NullIfEmpty(Str(msg, "quality"));
                if (quality != null && !CliParser.IsValidQualitySpec(quality))
                    return FailJson(Loc.T("cli.err.bad_args", "--quality"));

                var hash = NullIfEmpty(Str(msg, "hash"));
                if (hash != null && !CliParser.IsValidHashSpec(hash))
                    return FailJson(Loc.T("cli.err.bad_args", "--hash"));

                bool? startNow = msg["start"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) ? sb : null;
                var req = new CliAddRequest(
                    Url: url,
                    QueueName: NullIfEmpty(Str(msg, "queue")),
                    Folder: folder,
                    FileName: name,
                    Referer: referer,
                    StartNow: startNow,
                    Quality: quality,
                    ExpectedHash: hash);

                var (ok, message) = await target.AddDownloadAsync(req);
                return ok
                    ? new JsonObject { ["ok"] = true, ["message"] = message }
                    : FailJson(message);
            }

            case "add-file":
            {
                var urls = new List<string>();
                if (msg["urls"] is JsonArray arr)
                {
                    if (arr.Count > CliParser.MaxUrlsPerBatch)
                        return FailJson($"Too many URLs in batch (max {CliParser.MaxUrlsPerBatch})");

                    foreach (var node in arr)
                    {
                        if (node is JsonValue v && v.TryGetValue<string>(out var u) && !string.IsNullOrWhiteSpace(u))
                        {
                            if (!CliParser.IsValidDownloadUrl(u))
                                return FailJson(Loc.T("cli.err.invalid_url", u));
                            urls.Add(u.Trim());
                        }
                    }
                }
                else
                {
                    var filePath = Str(msg, "file");
                    if (string.IsNullOrWhiteSpace(filePath) || !CliParser.IsSafeFolderArg(filePath) || !File.Exists(filePath))
                        return FailJson("File not found");

                    var fi = new FileInfo(filePath);
                    if (fi.Length > CliParser.MaxUrlListFileBytes)
                        return FailJson("URL list file exceeds maximum size (4 MB)");

                    var parsed = CliParser.ParseUrlList(await File.ReadAllTextAsync(filePath), out var badLine);
                    if (badLine != null)
                        return FailJson(Loc.T("cli.err.invalid_url", "<invalid-line>"));
                    if (parsed.Count > CliParser.MaxUrlsPerBatch)
                        return FailJson($"Too many URLs in batch (max {CliParser.MaxUrlsPerBatch})");
                    urls.AddRange(parsed);
                }

                if (urls.Count == 0)
                    return FailJson("No valid URLs in list");

                int added = 0;
                string lastError = "";
                foreach (var u in urls)
                {
                    var (ok, message) = await target.AddDownloadAsync(new CliAddRequest(u));
                    if (ok) added++;
                    else lastError = message;
                }

                if (added == 0)
                    return FailJson(string.IsNullOrEmpty(lastError) ? "All URLs rejected" : lastError);

                return new JsonObject
                {
                    ["ok"] = true,
                    ["count"] = added,
                    ["message"] = Loc.T("cli.ok.added", added),
                };
            }

            case "start-queue":
            {
                var q = Str(msg, "queue");
                if (string.IsNullOrWhiteSpace(q))
                    return FailJson("Missing queue name");
                var (ok, message) = await target.StartQueueAsync(q);
                return ok
                    ? new JsonObject { ["ok"] = true, ["message"] = message }
                    : FailJson(message);
            }

            case "stop-queue":
            {
                var q = Str(msg, "queue");
                if (string.IsNullOrWhiteSpace(q))
                    return FailJson("Missing queue name");
                var (ok, message) = await target.StopQueueAsync(q);
                return ok
                    ? new JsonObject { ["ok"] = true, ["message"] = message }
                    : FailJson(message);
            }

            case "pause-all":
            {
                var (ok, message) = await target.PauseAllAsync();
                return ok
                    ? new JsonObject { ["ok"] = true, ["message"] = message }
                    : FailJson(message);
            }

            case "list":
            {
                var entries = await target.ListDownloadsAsync();
                var itemsArr = new JsonArray();
                var sb = new StringBuilder();
                foreach (var e in entries)
                {
                    itemsArr.Add(new JsonObject
                    {
                        ["id"] = e.Id.ToString(),
                        ["fileName"] = e.FileName,
                        ["url"] = e.Url,
                        ["status"] = e.Status,
                        ["downloaded"] = e.Downloaded,
                        ["totalSize"] = e.TotalSize,
                        ["queue"] = e.QueueName ?? "",
                    });
                    sb.AppendLine($"{e.FileName}\t{e.Status}\t{Fmt.Size(e.Downloaded)}/{Fmt.Size(e.TotalSize)}\t{e.Url}");
                }

                bool asJson = msg["json"] is JsonValue jv && jv.TryGetValue<bool>(out var jb) && jb;
                return new JsonObject
                {
                    ["ok"] = true,
                    ["items"] = itemsArr,
                    ["text"] = asJson ? itemsArr.ToJsonString(Json.Options) : sb.ToString().TrimEnd(),
                };
            }

            default:
                return FailJson($"Unknown CLI verb '{verb}'");
        }
    }

    public static int MapPipeReplyToExitCode(JsonObject? reply)
    {
        if (reply == null) return 3;
        bool ok = reply["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        return ok ? 0 : 4;
    }

    static string Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : "";

    static string? NullIfEmpty(string s)
        => string.IsNullOrWhiteSpace(s) ? null : s;

    static JsonObject FailJson(string error)
        => new() { ["ok"] = false, ["error"] = error };
}
