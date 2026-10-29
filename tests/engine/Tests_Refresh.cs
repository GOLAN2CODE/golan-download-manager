using System.Text.Json;
using Golan.Gdm.Core;

/// <summary>IDM "refresh download address": matching the re-clicked download, applying the new address, choosing samples.</summary>
static class Tests_Refresh
{
    static int _pass, _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  PASS  [REFRESH] {name}"); }
        else { _fail++; Console.WriteLine($"  FAIL  [REFRESH] {name}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
    }

    static DownloadItem Item(string name, long size) => new() { Url = "https://cdn1.example.com/f/" + name, FileName = name, TotalSize = size };

    public static Task<int> RunAsync()
    {
        Console.WriteLine("Refresh download address");
        const long mb = 1024 * 1024;

        // ---- matching ----
        var movie = Item("Movie.2026.mkv", 700 * mb);
        Check("same name and size: match by name", AddressRefresh.Match(movie, "Movie.2026.mkv", 700 * mb) == RefreshMatch.ByName);
        Check("name ignores case, spaces and the (1) of a duplicate copy",
            AddressRefresh.Match(movie, " movie.2026 (1).MKV ", 700 * mb) == RefreshMatch.ByName
            && AddressRefresh.Match(Item("Movie.2026 (2).mkv", 700 * mb), "Movie.2026.mkv", 700 * mb) == RefreshMatch.ByName);
        Check("same name, browser gives no size: match", AddressRefresh.Match(movie, "Movie.2026.mkv", -1) == RefreshMatch.ByName);
        Check("same name but another size: never glued onto the old part", AddressRefresh.Match(movie, "Movie.2026.mkv", 701 * mb) == RefreshMatch.None);
        Check("random new name, same big size: match by size", AddressRefresh.Match(movie, "dl_8f2a91.bin", 700 * mb) == RefreshMatch.BySize);
        Check("random name and tiny equal size: too weak, no match", AddressRefresh.Match(Item("a.txt", 300), "b.txt", 300) == RefreshMatch.None);
        Check("other name, size unknown: no match", AddressRefresh.Match(movie, "other.zip", -1) == RefreshMatch.None);
        Check("item size unknown, same name: match", AddressRefresh.Match(Item("setup.exe", -1), "setup.exe", 5 * mb) == RefreshMatch.ByName);
        Check("empty names never match", AddressRefresh.Match(Item("", 5 * mb), "", -1) == RefreshMatch.None);
        Check("name key keeps the extension and real numbers", AddressRefresh.NameKey("Report (2026).pdf") == "report (2026).pdf" && AddressRefresh.NameKey("x (3).zip") == "x.zip");

        // ---- which items can take a new address ----
        Check("ordinary paused download can be refreshed", AddressRefresh.CanRefresh(new DownloadItem { Url = "https://a.example/f.zip", Status = DownloadStatus.Error }));
        Check("videos, torrents and finished files cannot",
            !AddressRefresh.CanRefresh(new DownloadItem { Url = "https://youtube.com/watch?v=x", Kind = DownloadKind.Media })
            && !AddressRefresh.CanRefresh(new DownloadItem { Url = "magnet:?xt=urn:btih:abc", Kind = DownloadKind.Torrent })
            && !AddressRefresh.CanRefresh(new DownloadItem { Url = "https://a.example/f.zip", Status = DownloadStatus.Completed }));

        // ---- applying the fresh address ----
        var it = Item("Movie.2026.mkv", 700 * mb);
        it.Cookies = "old=1"; it.CookieHost = "cdn1.example.com";
        it.Referrer = "https://site.example/page"; it.UserAgent = "UA-old";
        it.Status = DownloadStatus.Error; it.ErrorMessage = "403"; it.RetryCount = 2;
        it.Segments.Add(new Segment { Start = 0, End = 700 * mb - 1, Downloaded = 300 * mb });
        it.ResolvedUrl = "https://cdn1.example.com/expired?sig=1";
        AddressRefresh.Apply(it, " https://cdn7.example.com/f/Movie.2026.mkv?sig=2 ", "", "new=2", "UA-new", "https://site.example/page2");
        Check("new address stored, old redirect target forgotten", it.Url == "https://cdn7.example.com/f/Movie.2026.mkv?sig=2" && it.ResolvedUrl == null);
        Check("new cookies belong to the new host only", it.Cookies == "new=2" && it.CookieHost == "cdn7.example.com");
        Check("empty referrer keeps the old one; agent and page updated", it.Referrer == "https://site.example/page" && it.UserAgent == "UA-new" && it.ParentPage == "https://site.example/page2");
        Check("error cleared and ready to resume", it.Status == DownloadStatus.Paused && it.ErrorMessage == "" && it.RetryCount == 0);
        Check("downloaded parts are kept", it.Segments.Count == 1 && it.Segments[0].Downloaded == 300 * mb);
        Check("item must prove the bytes before resuming", it.AddressRefreshed);
        AddressRefresh.Apply(it, "https://cdn8.example.com/x", "", "", "");
        Check("no cookies from the browser: the previous ones stay", it.Cookies == "new=2");

        var back = JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.Serialize(it, Json.Options), Json.Options)!;
        Check("the check survives a restart of Golan", back.AddressRefreshed);
        it.LastFailKind = FailKind.Expired;
        back = JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.Serialize(it, Json.Options), Json.Options)!;
        Check("the last failure reason is not saved", back.LastFailKind == FailKind.General);
        it.ResetProgress();
        Check("starting over clears the check", !it.AddressRefreshed);

        Check("source page: parent page, then referrer, never a non-web value",
            AddressRefresh.SourcePage(new DownloadItem { ParentPage = "https://p.example/", Referrer = "https://r.example/" }) == "https://p.example/"
            && AddressRefresh.SourcePage(new DownloadItem { ParentPage = "", Referrer = "https://r.example/" }) == "https://r.example/"
            && AddressRefresh.SourcePage(new DownloadItem { ParentPage = "javascript:alert(1)", Referrer = "file:///c:/x" }) == "");

        // ---- waiting for the click ----
        var r = new AddressRefresh();
        var t0 = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var ended = new List<Guid>();
        r.Ended += ended.Add;
        r.Begin(a, t0);
        Check("waits for the item", r.Pending(t0.AddMinutes(9)) == a && r.Remaining(t0.AddMinutes(9)) == TimeSpan.FromMinutes(1));
        Check("gives up after ten minutes", r.Pending(t0.AddMinutes(10).AddSeconds(1)) == null);
        r.Begin(a, t0);
        r.Begin(b, t0);
        Check("only one item waits at a time; the replaced one is told", r.Pending(t0) == b && ended.SequenceEqual([a]));
        r.Cancel(a);
        Check("cancelling another item does nothing", r.Pending(t0) == b);
        r.Cancel(b);
        Check("cancel ends the wait", r.Pending(t0) == null && ended.SequenceEqual([a, b]));

        // ---- byte samples a refreshed address must reproduce ----
        var s = new DownloadItem { TotalSize = 10 * mb };
        s.Segments.AddRange([
            new Segment { Start = 0, End = 5 * mb - 1, Downloaded = 3 * mb },
            new Segment { Start = 5 * mb, End = 10 * mb - 1, Downloaded = 4 * mb },
        ]);
        var samples = HttpDownloader.RefreshSamples(s);
        bool inside = samples.All(x => s.Segments.Any(g => x.From >= g.Start && x.From + x.Length <= g.Position));
        bool apart = samples.All(x => samples.Count(y => x.From < y.From + y.Length && y.From < x.From + x.Length) == 1);
        Check("samples: the file start plus the end of each part, only downloaded bytes, no overlap",
            samples.Count == 3 && samples[0] == (0, HttpDownloader.RefreshSampleSize) && inside && apart, string.Join(";", samples));
        var tiny = new DownloadItem { TotalSize = 100 };
        tiny.Segments.Add(new Segment { Start = 0, End = 99, Downloaded = 40 });
        var ts = HttpDownloader.RefreshSamples(tiny);
        Check("small parts give one short sample", ts.Count == 1 && ts[0] == (0, 40), string.Join(";", ts));
        Check("nothing downloaded: nothing to compare", HttpDownloader.RefreshSamples(new DownloadItem()).Count == 0);

        Console.WriteLine($"[REFRESH Summary] {_pass} passed, {_fail} failed");
        return Task.FromResult(_fail);
    }
}
