using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Golan.Gdm.Core;
using Golan.Gdm.Core.Torrent;

namespace Golan.Gdm.Integration;

/// <summary>F15: Console attachment, CLI runner, and DownloadManager adapter for ICliTarget.</summary>
public static class CliRunner
{
    const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    public static void TryAttachParentConsole()
    {
        try
        {
            if (!Console.IsOutputRedirected && !Console.IsErrorRedirected)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // Ignore when running without a parent console or when redirected
        }
    }

    public static int RunFromMain(string[] args)
    {
        TryAttachParentConsole();
        try
        {
            var parsed = CliParser.Parse(args);
            if (!parsed.Ok || parsed.Command == null)
            {
                WriteErr(parsed.ErrorMessage ?? Loc.T("cli.err.bad_args", ""));
                return parsed.ExitCode != 0 ? parsed.ExitCode : 2;
            }

            var cmd = parsed.Command;
            if (string.Equals(cmd.Verb, "help", StringComparison.OrdinalIgnoreCase))
            {
                WriteOut(CliParser.GetHelpText());
                return 0;
            }

            if (!string.IsNullOrWhiteSpace(cmd.Folder))
            {
                try
                {
                    cmd = cmd with { Folder = Path.GetFullPath(cmd.Folder.Trim()) };
                }
                catch (Exception ex)
                {
                    WriteErr(Loc.T("cli.err.bad_args", ex.Message));
                    return 2;
                }
            }

            List<string>? resolvedUrls = null;
            if (string.Equals(cmd.Verb, "add-file", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = cmd.FilePath ?? "";
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    WriteErr(Loc.T("cli.err.bad_args", $"file not found: {filePath}"));
                    return 2;
                }

                var fi = new FileInfo(filePath);
                if (fi.Length > CliParser.MaxUrlListFileBytes)
                {
                    WriteErr(Loc.T("cli.err.bad_args", "URL list file exceeds 4 MB limit"));
                    return 2;
                }

                string content;
                try
                {
                    content = File.ReadAllText(filePath, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    WriteErr(Loc.T("cli.err.bad_args", ex.Message));
                    return 2;
                }

                resolvedUrls = CliParser.ParseUrlList(content, out var invalidLine);
                if (invalidLine != null)
                {
                    WriteErr(Loc.T("cli.err.invalid_url", invalidLine));
                    return 2;
                }
                if (resolvedUrls.Count == 0)
                {
                    WriteErr(Loc.T("cli.err.bad_args", "no URLs in file"));
                    return 2;
                }
                if (resolvedUrls.Count > CliParser.MaxUrlsPerBatch)
                {
                    WriteErr(Loc.T("cli.err.bad_args", $"too many URLs (max {CliParser.MaxUrlsPerBatch})"));
                    return 2;
                }
            }

            var pipeMsg = cmd.ToPipeMessage(resolvedUrls);
            var reply = CliIpc.SendOrStartAsync(pipeMsg, 10_000).GetAwaiter().GetResult();
            int exitCode = CliActions.MapPipeReplyToExitCode(reply);

            if (reply == null)
            {
                WriteErr(Loc.T("cli.err.unreachable"));
                return 3;
            }

            if (exitCode == 0)
            {
                var text = reply["text"]?.GetValue<string>() ?? reply["message"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(text))
                    WriteOut(text);
                return 0;
            }
            else
            {
                var err = reply["error"]?.GetValue<string>() ?? "Rejected";
                WriteErr(Loc.T("cli.err.rejected", err));
                return 4;
            }
        }
        finally
        {
            try
            {
                Console.Out.Flush();
                Console.Error.Flush();
            }
            catch (IOException)
            {
                // Best-effort console flush on WinExe exit (handle may be closed or unattached).
            }
        }
    }

    static void WriteOut(string text)
    {
        try { Console.Out.WriteLine(text); }
        catch (IOException) { /* Best-effort stdout write on WinExe */ }
    }

    static void WriteErr(string text)
    {
        try { Console.Error.WriteLine(text); }
        catch (IOException) { /* Best-effort stderr write on WinExe */ }
    }
}

/// <summary>Default ICliTarget implementation backed by DownloadManager on the UI thread.</summary>
public sealed class DownloadManagerCliTarget : ICliTarget
{
    readonly DownloadManager _manager;
    readonly Action<DownloadItem, CliAddRequest>? _configureItem;

    public DownloadManagerCliTarget(DownloadManager manager, Action<DownloadItem, CliAddRequest>? configureItem = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _configureItem = configureItem;
    }

    public Task<(bool Ok, string Message)> AddDownloadAsync(CliAddRequest request)
    {
        try
        {
            if (!CliParser.IsValidDownloadUrl(request.Url))
                return Task.FromResult((false, Loc.T("cli.err.invalid_url", request.Url)));

            if (!string.IsNullOrWhiteSpace(request.Folder) && !CliParser.IsSafeFolderArg(request.Folder))
                return Task.FromResult((false, Loc.T("cli.err.bad_args", "--folder")));

            if (!string.IsNullOrWhiteSpace(request.FileName) && !CliParser.IsSafeFileNameArg(request.FileName))
                return Task.FromResult((false, Loc.T("cli.err.bad_args", "--name")));

            if (!string.IsNullOrWhiteSpace(request.Referer) && !FileNames.IsHttpUrl(request.Referer))
                return Task.FromResult((false, Loc.T("cli.err.bad_args", "--referer")));

            if (!string.IsNullOrWhiteSpace(request.Quality) && !CliParser.IsValidQualitySpec(request.Quality))
                return Task.FromResult((false, Loc.T("cli.err.bad_args", "--quality")));

            if (!string.IsNullOrWhiteSpace(request.ExpectedHash) && !CliParser.IsValidHashSpec(request.ExpectedHash))
                return Task.FromResult((false, Loc.T("cli.err.bad_args", "--hash")));

            if (_manager.Settings.IsException(request.Url))
                return Task.FromResult((false, Loc.T("cli.err.rejected", request.Url)));

            Guid? queueId = null;
            if (!string.IsNullOrWhiteSpace(request.QueueName))
            {
                var q = FindQueueByName(_manager.Settings, request.QueueName);
                if (q == null)
                    return Task.FromResult((false, $"Queue not found: {request.QueueName}"));
                queueId = q.Id;
            }

            var safeName = !string.IsNullOrWhiteSpace(request.FileName)
                ? FileNames.Sanitize(request.FileName)
                : "";

            string? fullFolder = null;
            if (!string.IsNullOrWhiteSpace(request.Folder))
            {
                fullFolder = Path.GetFullPath(request.Folder.Trim());
                Directory.CreateDirectory(fullFolder);
            }

            bool startNow = request.StartNow ?? (queueId == null);

            if (request.Url.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                if (!MagnetValidator.IsMagnetUri(request.Url))
                    return Task.FromResult((false, Loc.T("cli.err.invalid_url", request.Url)));
                var magItem = _manager.AddMagnet(request.Url, saveDir: fullFolder, startNow: false);
                if (!string.IsNullOrWhiteSpace(safeName))
                {
                    magItem.FileName = safeName;
                    magItem.NameLocked = true;
                }
                if (!string.IsNullOrWhiteSpace(request.ExpectedHash))
                    magItem.ExpectedHash = request.ExpectedHash.Trim();
                if (queueId != null)
                    magItem.QueueId = queueId;
                _configureItem?.Invoke(magItem, request);
                _manager.Save();
                if (startNow)
                    _manager.Start(magItem);
                return Task.FromResult((true, Loc.T("cli.ok.added", 1)));
            }

            var item = _manager.CreateHttp(
                request.Url,
                referrer: request.Referer ?? "",
                fileName: safeName);

            if (!string.IsNullOrWhiteSpace(safeName))
                item.NameLocked = true;

            if (fullFolder != null)
                item.SaveDir = fullFolder;

            if (!string.IsNullOrWhiteSpace(request.Quality))
            {
                var qVal = request.Quality.Trim().TrimEnd('p', 'P');
                item.MediaQuality = qVal;
                item.MediaOptions = _manager.Settings.CreateDefaultMediaOptions(qVal);
                if (MediaDownloader.LooksLikeMediaPage(request.Url))
                    item.Kind = DownloadKind.Media;
            }

            if (!string.IsNullOrWhiteSpace(request.ExpectedHash))
                item.ExpectedHash = request.ExpectedHash.Trim();

            _configureItem?.Invoke(item, request);

            _manager.Add(item, startNow, queueId);
            if (request.StartNow == true && queueId != null)
                _manager.Start(item);

            return Task.FromResult((true, Loc.T("cli.ok.added", 1)));
        }
        catch (Exception ex)
        {
            return Task.FromResult((false, ex.Message));
        }
    }

    public Task<(bool Ok, string Message)> StartQueueAsync(string queueName)
    {
        var q = FindQueueByName(_manager.Settings, queueName);
        if (q == null)
            return Task.FromResult((false, $"Queue not found: {queueName}"));

        _manager.StartQueue(q.Id);
        return Task.FromResult((true, Loc.T("cli.ok.queue_started", q.Name)));
    }

    public Task<(bool Ok, string Message)> StopQueueAsync(string queueName)
    {
        var q = FindQueueByName(_manager.Settings, queueName);
        if (q == null)
            return Task.FromResult((false, $"Queue not found: {queueName}"));

        _manager.StopQueue(q.Id);
        return Task.FromResult((true, Loc.T("cli.ok.queue_stopped", q.Name)));
    }

    public Task<(bool Ok, string Message)> PauseAllAsync()
    {
        _manager.PauseAll();
        return Task.FromResult((true, Loc.T("cli.ok.paused_all")));
    }

    public Task<IReadOnlyList<CliListEntry>> ListDownloadsAsync()
    {
        var list = _manager.Items.Select(i =>
        {
            var qName = i.QueueId is { } qid ? _manager.Settings.FindQueue(qid)?.Name : null;
            return new CliListEntry(
                i.Id,
                i.FileName,
                i.Url,
                i.Status.ToString(),
                i.Downloaded,
                i.TotalSize,
                qName);
        }).ToList();

        return Task.FromResult<IReadOnlyList<CliListEntry>>(list);
    }

    static QueueDef? FindQueueByName(AppSettings settings, string name)
    {
        if (settings.Queues.Count == 0)
            settings.Normalize();
        var trimmed = name.Trim();
        if (string.Equals(trimmed, "main", StringComparison.OrdinalIgnoreCase))
            return settings.FindQueue(QueueDef.MainId);
        if (string.Equals(trimmed, "sync", StringComparison.OrdinalIgnoreCase))
            return settings.FindQueue(QueueDef.SyncId);

        return settings.Queues.FirstOrDefault(q =>
            string.Equals(q.Name, trimmed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(q.Id.ToString(), trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
