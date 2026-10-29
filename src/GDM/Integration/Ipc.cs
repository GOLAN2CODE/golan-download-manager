using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Golan.Gdm.Core;

namespace Golan.Gdm.Integration;

/// <summary>Local named pipe used by the browser host and by second instances. Restricted to the current user.</summary>
public static class Ipc
{
    public const int MaxLineChars = 16 * 1024 * 1024;
    static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Per-user pipe (and single-instance mutex) name. An isolated run (GDM_HOME / GOLAN_HOME, used by tests) gets its own
    /// name so it never talks to, or is blocked by, the user's real Golan.
    /// </summary>
    public static string PipeName { get; } = "Golan.GDM." + Sanitize(Environment.UserName) + IsolationSuffix();

    static string IsolationSuffix()
    {
        var home = AppPaths.GetIsolationHomeEnv();
        if (string.IsNullOrWhiteSpace(home)) return "";
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(home.Trim()).ToUpperInvariant()));
        return "." + Convert.ToHexString(hash, 0, 6);
    }

    static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    public static async Task<JsonObject?> SendAsync(JsonObject message, int connectTimeoutMs, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(connectTimeoutMs, ct); }
        catch (TimeoutException) { return null; }

        var utf8 = new UTF8Encoding(false);
        using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, utf8, leaveOpen: true);
        await writer.WriteLineAsync(message.ToJsonString());
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(ReadTimeout);
        var line = await ReadBoundedLineAsync(reader, MaxLineChars, readCts.Token);
        return line == null ? null : JsonNode.Parse(line) as JsonObject;
    }

    internal static async Task<string?> ReadBoundedLineAsync(StreamReader reader, int maxChars, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(), ct);
            if (n == 0)
                return sb.Length > 0 ? sb.ToString() : null;

            for (int i = 0; i < n; i++)
            {
                char c = buf[i];
                if (c == '\n')
                {
                    if (sb.Length > 0 && sb[^1] == '\r')
                        sb.Length -= 1;
                    return sb.ToString();
                }
                if (sb.Length >= maxChars)
                    throw new InvalidDataException("IPC message exceeded maximum allowed length.");
                sb.Append(c);
            }
        }
    }
}

/// <summary>F15 helper: forwards a CLI pipe message to the running instance, starting Golan in the background if needed.</summary>
public static class CliIpc
{
    public static async Task<JsonObject?> SendOrStartAsync(
        JsonObject message,
        int waitTimeoutMs = 10_000,
        CancellationToken ct = default)
    {
        var reply = await Ipc.SendAsync(message, 400, ct);
        if (reply != null)
            return reply;

        try
        {
            var exe = AppPaths.ExePath;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe, "--background")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
        }
        catch (Exception ex)
        {
            Log.Write("cli auto-start failed: " + ex.Message);
            return null;
        }

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < waitTimeoutMs && !ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct);
            reply = await Ipc.SendAsync(message, 300, ct);
            if (reply != null)
                return reply;
        }

        return null;
    }
}

public sealed class PipeServer
{
    readonly Func<JsonObject, Task<JsonObject>> _handler;
    readonly CancellationTokenSource _cts = new();

    public PipeServer(Func<JsonObject, Task<JsonObject>> handler) => _handler = handler;

    public void Start() => _ = Task.Run(LoopAsync);
    public void Stop() => _cts.Cancel();

    async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(Ipc.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                var connected = server;
                server = null;
                _ = Task.Run(() => HandleAsync(connected));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Write("pipe error: " + ex.Message);
                await Task.Delay(500);
            }
            finally { server?.Dispose(); }
        }
    }

    async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        try
        {
            var utf8 = new UTF8Encoding(false);
            using var reader = new StreamReader(pipe, utf8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, utf8, leaveOpen: true) { AutoFlush = true };
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            readCts.CancelAfter(TimeSpan.FromSeconds(15));
            var line = await Ipc.ReadBoundedLineAsync(reader, Ipc.MaxLineChars, readCts.Token);
            if (line == null) return;
            JsonObject response;
            try
            {
                var msg = JsonNode.Parse(line) as JsonObject ?? [];
                response = await _handler(msg);
            }
            catch (Exception ex)
            {
                response = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
            }
            await writer.WriteLineAsync(response.ToJsonString());
        }
        catch (Exception ex) { Log.Write("pipe client error: " + ex.Message); }
    }
}

/// <summary>Chrome native-messaging host. Chrome starts GDM.exe with the extension origin as argument.</summary>
public static class NativeHost
{
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    const int ASFW_ANY = -1;

    public static int Run()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        return Run(stdin, stdout);
    }

    /// <summary>
    /// The extension keeps one port open (like IDM), so this helper lives as long as the browser and answers
    /// each message as soon as it is ready, tagged with the request number it came with ("_rid"). A one-shot
    /// message (no number) works as before. When the browser closes the port, unfinished answers are completed first.
    /// </summary>
    public static int Run(Stream stdin, Stream stdout)
    {
        var inFlight = new List<Task>();
        var writeLock = new object();
        try
        {
            while (true)
            {
                var header = ReadExactly(stdin, 4);
                if (header == null) break;
                int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
                if (len <= 0 || len > 1024 * 1024) break;
                var body = ReadExactly(stdin, len);
                if (body == null) break;
                inFlight.RemoveAll(t => t.IsCompleted);
                inFlight.Add(Task.Run(() => AnswerAsync(body, stdout, writeLock)));
            }
            Task.WaitAll([.. inFlight], TimeSpan.FromMinutes(11));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("native host error: " + ex);
            return 1;
        }
    }

    static async Task AnswerAsync(byte[] body, Stream stdout, object writeLock)
    {
        JsonObject reply;
        JsonNode? requestId = null;
        try
        {
            var msg = JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject ?? [];
            if (msg.TryGetPropertyValue("_rid", out var rid))
            {
                requestId = rid?.DeepClone();
                msg.Remove("_rid");
            }
            reply = await HandleAsync(msg);
        }
        catch (Exception ex)
        {
            reply = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
        if (requestId != null) reply["_rid"] = requestId;
        var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
        lock (writeLock)
        {
            try
            {
                stdout.Write(BitConverter.GetBytes(bytes.Length));
                stdout.Write(bytes);
                stdout.Flush();
            }
            catch (IOException) { /* the browser closed the port */ }
        }
    }

    static readonly Lazy<string> LaunchingBrowser = new(ExtensionContact.DetectLaunchingBrowser);

    static byte[]? ReadExactly(Stream s, int count)
    {
        var buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buf, read, count - read);
            if (n == 0) return null;
            read += n;
        }
        return buf;
    }

    static async Task<JsonObject> HandleAsync(JsonObject msg)
    {
        var action = msg["action"]?.GetValue<string>() ?? "";
        if (action is "download" or "add" or "downloadAll" or "media" or "show" or "torrent")
            AllowSetForegroundWindow(ASFW_ANY);
        if (action == "ping")
            msg["browser"] = LaunchingBrowser.Value; // lets GDM's guide confirm which browser is connected

        var reply = await Ipc.SendAsync(msg, 400);
        if (reply != null) return reply;

        if (action == "ping")
        {
            // Golan is closed: answer from the saved settings so interception keeps working; it starts on demand.
            var s = AppSettings.Load();
            var r = App.PingReply(s);
            r["running"] = false;
            return r;
        }

        Process.Start(new ProcessStartInfo(AppPaths.ExePath, "--background") { UseShellExecute = true });
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            reply = await Ipc.SendAsync(msg, 300);
            if (reply != null) return reply;
        }
        return new JsonObject { ["ok"] = false, ["error"] = Loc.T("main.ipc.startFailed") };
    }
}
