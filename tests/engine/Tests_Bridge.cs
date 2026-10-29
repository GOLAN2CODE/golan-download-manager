using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Golan.Gdm.Integration;

/// <summary>The browser helper with one open port: many messages at once, answers tagged with their request number.</summary>
static class Tests_Bridge
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  [BRIDGE] {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  [BRIDGE] {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
    }

    static void WriteFrame(Stream s, JsonObject msg)
    {
        var body = Encoding.UTF8.GetBytes(msg.ToJsonString());
        s.Write(BitConverter.GetBytes(body.Length));
        s.Write(body);
    }

    static List<JsonObject> ReadFrames(byte[] data)
    {
        var list = new List<JsonObject>();
        int pos = 0;
        while (pos + 4 <= data.Length)
        {
            int len = BitConverter.ToInt32(data, pos);
            pos += 4;
            if (len <= 0 || pos + len > data.Length) break;
            if (JsonNode.Parse(Encoding.UTF8.GetString(data, pos, len)) is JsonObject o) list.Add(o);
            pos += len;
        }
        return list;
    }

    public static async Task<int> RunAsync()
    {
        Console.WriteLine("Browser helper (open port)");

        // Golan is not running in this isolated run, so every ping waits ~0.4 s for it before answering from
        // the saved settings: five one-by-one answers would take at least 2 s.
        var input = new MemoryStream();
        for (int i = 1; i <= 4; i++) WriteFrame(input, new JsonObject { ["action"] = "ping", ["_rid"] = i });
        WriteFrame(input, new JsonObject { ["action"] = "ping" }); // a one-shot message from an older extension
        input.Position = 0;
        var output = new MemoryStream();
        var sw = Stopwatch.StartNew();
        int code = await Task.Run(() => NativeHost.Run(input, output));
        sw.Stop();
        var replies = ReadFrames(output.ToArray());

        Check("every message is answered before the helper exits", code == 0 && replies.Count == 5, $"code={code} replies={replies.Count}");
        var ids = replies.Where(r => r["_rid"] != null).Select(r => r["_rid"]!.GetValue<int>()).OrderBy(x => x).ToList();
        Check("answers carry their own request number", ids.SequenceEqual([1, 2, 3, 4]), string.Join(",", ids));
        Check("a message without a number gets an answer without one", replies.Count(r => r["_rid"] == null) == 1);
        Check("all answers come from the saved settings while Golan is closed", replies.All(r => r["ok"]?.GetValue<bool>() == true && r["running"]?.GetValue<bool>() == false));
        Check("messages are answered side by side, not one after another", sw.ElapsedMilliseconds < 1800, $"{sw.ElapsedMilliseconds} ms");

        var empty = new MemoryStream();
        Check("a closed port ends the helper quietly", NativeHost.Run(new MemoryStream(), empty) == 0 && empty.Length == 0);

        Console.WriteLine($"[BRIDGE Summary] {_pass} passed, {_fail} failed");
        return _fail;
    }
}
