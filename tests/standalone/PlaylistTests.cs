using BDownloader.Core;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

// Standalone regression host. Every fixture uses Bilibili's actual series or
// seasons_archives_list response schema, with deterministic synthetic videos.
internal static class PlaylistTests
{
    private const string UserUrl = "https://www.bilibili.com/list/161775300/?sid=1774529&spm_id_from=333.1387.0.0&oid=1956380285&bvid=BV1Ky411q7QC";
    private static int failures;

    public static int Main(string[] args)
    {
        if (args.Contains("--online")) return VerifyProvidedListOnline();
        Check("modern player expands every page, ignoring current bvid and oid", async () =>
        {
            var requests = new List<string>();
            var resolver = Fixture(65, false, requests);
            PlaylistResult result = await resolver.ResolveAsync(UserUrl, CancellationToken.None);
            Require(result.IsComplete && result.ExpectedCount == 65 && result.Entries.Count == 65, "playlist was truncated");
            Require(result.Title == "回归测试列表", "title missing");
            Require(result.Entries.Select(v => v.Id).Distinct().Count() == 65, "entry IDs are not unique");
            Require(result.Entries[64].Url.EndsWith("BV0000000065"), "third page is missing");
            Require(result.Entries[0].DurationSeconds == 101, "duration missing");
            Require(requests.Count == 4 && requests.Last().Contains("pn=3"), "did not request all three archive pages");
            Require(!requests.Any(u => u.Contains("oid=") || u.Contains("bvid=")), "current video incorrectly limited the list");
            Require(requests.Skip(1).All(u => u.Contains("only_normal=false")), "unavailable entries were silently filtered");
        });

        Check("multi-part video lists every page with its own title and duration", async () =>
        {
            var data = JObject.Parse("{ 'title':'Course', 'videos':3, 'pages':[ {'page':1,'part':'Intro','duration':120}, {'page':2,'part':'','duration':90}, {'page':3,'part':'Review','duration':60} ] }");
            PlaylistResult result = BilibiliPlaylistResolver.ParseVideoPages(data, "BV1WTnozqEd3");
            Require(result.IsComplete && result.ExpectedCount == 3 && result.Entries.Count == 3, "page count not verified");
            Require(result.Entries[1].Url == "https://www.bilibili.com/video/BV1WTnozqEd3?p=2", "page URL missing");
            Require(result.Entries.Select(v => v.Id).Distinct().Count() == 3, "page IDs are not unique");
            Require(result.Entries[0].Title == "P1 Intro" && result.Entries[2].DurationSeconds == 60, "titles or durations missing");
            bool rejected = false;
            try { BilibiliPlaylistResolver.ParseVideoPages(JObject.Parse("{ 'videos':4, 'pages':[ {'page':1} ] }"), "BV1WTnozqEd3"); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "mismatched page total was accepted");
        });

        Check("explicit season chooses season namespace and all pages", async () =>
        {
            var requests = new List<string>();
            PlaylistResult result = await Fixture(61, true, requests).ResolveAsync(UserUrl + "&type=season", CancellationToken.None);
            Require(result.Entries.Count == 61 && result.IsComplete, "season was truncated");
            Require(requests.Count == 3 && requests.All(u => u.Contains("seasons_archives_list")), "season was treated as a series");
            Require(requests.Last().Contains("page_num=3"), "season paging parameter incorrect");
        });

        Check("space lists uses explicit series parameter", async () =>
        {
            var requests = new List<string>();
            await Fixture(31, false, requests).ResolveAsync("https://space.bilibili.com/161775300/lists/1774529?type=series", CancellationToken.None);
            Require(requests.First().Contains("/x/series/series?") && requests.Last().Contains("pn=2"), "series type ignored");
        });

        Check("space lists defaults to season", async () =>
        {
            var requests = new List<string>();
            await Fixture(1, true, requests).ResolveAsync("https://space.bilibili.com/161775300/lists/1774529", CancellationToken.None);
            Require(requests.Single().Contains("season_id=1774529"), "space default is incorrect");
        });

        Check("missing default series can resolve a collection", async () =>
        {
            var resolver = new BilibiliPlaylistResolver((url, token) => Task.FromResult(url.Contains("/x/series/series?")
                ? "{\"code\":-404,\"message\":\"not found\"}" : Page(true, 1, 1, 30, 1, 1).ToString()));
            PlaylistResult result = await resolver.ResolveAsync(UserUrl, CancellationToken.None);
            Require(result.IsComplete && result.Entries.Count == 1, "collection fallback failed");
        });

        Check("API risk block is reported without namespace fallback", async () =>
        {
            int requests = 0;
            var resolver = new BilibiliPlaylistResolver((url, token) => { requests++; return Task.FromResult("{\"code\":-352,\"message\":\"risk control\"}"); });
            await Reject(resolver, UserUrl, "-352");
            Require(requests == 1, "risk failure was disguised as another list type");
        });

        Check("private playlist reports login error", async () =>
        {
            var resolver = new BilibiliPlaylistResolver((url, token) => Task.FromResult("{\"code\":-101,\"message\":\"not logged in\"}"));
            await Reject(resolver, UserUrl + "&type=season", "-101");
        });

        Check("early empty page cannot succeed as an incomplete playlist", async () =>
        {
            var resolver = Transform(31, (data, page) => { if (page == 2) data["archives"] = new JArray(); });
            await Reject(resolver, UserUrl, "30 / 31");
        });

        Check("short first page with nonfinal total is rejected", async () =>
        {
            var resolver = Transform(31, (data, page) => { if (page == 1) ((JArray)data["archives"]).RemoveAt(0); });
            await Reject(resolver, UserUrl, "提前结束");
        });

        Check("repeated page is rejected instead of duplicating videos", async () =>
        {
            var resolver = Transform(31, (data, page) => { if (page == 2) data["archives"] = Page(false, 1, 31, 30, 1, 1)["data"]["archives"]; });
            await Reject(resolver, UserUrl, "重复");
        });

        Check("playlist changing during pagination is rejected", async () =>
        {
            var resolver = Transform(31, (data, page) => { if (page == 2) data["page"]["total"] = 32; });
            await Reject(resolver, UserUrl, "总数发生变化");
        });

        Check("response without a total cannot claim completeness", async () =>
        {
            var resolver = Transform(1, (data, page) => ((JObject)data["page"]).Remove("total"));
            await Reject(resolver, UserUrl, "未提供列表总数");
        });

        Check("metadata total catches server filtering of unavailable videos", async () =>
        {
            var resolver = new BilibiliPlaylistResolver((url, token) => Task.FromResult(url.Contains("/x/series/series?")
                ? Metadata(2) : Page(false, 1, 1, 30, 1, 1).ToString()));
            await Reject(resolver, UserUrl, "元数据和分页总数不一致");
        });

        Check("unidentifiable entry is reported, never skipped", async () =>
        {
            var resolver = Transform(1, (data, page) => data["archives"][0] = new JObject { ["title"] = "已失效视频" });
            await Reject(resolver, UserUrl, "失效或无法识别");
        });

        Check("numeric aid fallback produces a separate canonical video URL", async () =>
        {
            var resolver = Transform(1, (data, page) => { ((JObject)data["archives"][0]).Remove("bvid"); });
            PlaylistResult result = await resolver.ResolveAsync(UserUrl, CancellationToken.None);
            Require(result.Entries[0].Url == "https://www.bilibili.com/video/av100001", "aid URL is wrong");
        });

        Check("HTML risk-control response is an explicit failure", async () =>
        {
            var resolver = new BilibiliPlaylistResolver((url, token) => Task.FromResult("<html>412</html>"));
            await Reject(resolver, UserUrl, "非 JSON");
        });

        Check("cancelled parsing never returns a partial playlist", async () =>
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var resolver = new BilibiliPlaylistResolver((url, token) =>
                {
                    if (url.Contains("pn=2")) cancellation.Cancel();
                    return Task.FromResult(url.Contains("/x/series/series?") ? Metadata(31) : Page(false, QueryPage(url), 31, 30, QueryPage(url) == 1 ? 1 : 31, QueryPage(url) == 1 ? 30 : 1).ToString());
                });
                try { await resolver.ResolveAsync(UserUrl, cancellation.Token); throw new Exception("cancellation was ignored"); }
                catch (OperationCanceledException) { }
            }
        });

        Check("URL recognition is bounded to Bilibili list routes", () =>
        {
            Require(BilibiliPlaylistResolver.IsListUrl(UserUrl), "provided URL not recognized");
            Require(BilibiliPlaylistResolver.IsListUrl(UserUrl.Replace("&", "&amp;")), "HTML escaped URL not recognized");
            Require(BilibiliPlaylistResolver.IsListUrl("https://space.bilibili.com/161775300/channel/seriesdetail?sid=1774529"), "legacy series route not recognized");
            foreach (string invalid in new[] { null, "", "https://evil.example/list/1?sid=2", "https://bilibili.com.evil.example/list/1?sid=2", "https://user:secret@www.bilibili.com/list/161775300/?sid=1774529", "https://www.bilibili.com/video/BV1Ky411q7QC", "https://www.bilibili.com/list/1", UserUrl + "&sid=3", UserUrl + "&type=unknown" })
                Require(!BilibiliPlaylistResolver.IsListUrl(invalid), "invalid URL accepted: " + invalid);
            return Task.CompletedTask;
        });

        Console.WriteLine("RESULT failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static int VerifyProvidedListOnline()
    {
        Check("provided Bilibili list resolves every live page", async () =>
        {
            int requests = 0;
            var resolver = new BilibiliPlaylistResolver(async (url, token) =>
            {
                requests++;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 20000;
                request.UserAgent = "Mozilla/5.0";
                request.Referer = "https://www.bilibili.com/";
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (token.Register(request.Abort))
                using (var response = await request.GetResponseAsync())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return await reader.ReadToEndAsync();
            });
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                PlaylistResult result = await resolver.ResolveAsync(UserUrl, cancellation.Token);
                Require(result.IsComplete && result.ExpectedCount == result.Entries.Count && result.Entries.Count > 30, "live playlist is incomplete");
                Require(result.Entries.Select(v => v.Id).Distinct().Count() == result.ExpectedCount, "duplicate live entries");
                Require(result.Entries.Any(v => v.Id == "BV1Ky411q7QC"), "provided current video absent from resolved playlist");
                Console.WriteLine("LIVE title=" + result.Title + " count=" + result.Entries.Count + " expected=" + result.ExpectedCount + " requests=" + requests);
            }
        });
        Console.WriteLine("RESULT failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static BilibiliPlaylistResolver Fixture(int total, bool season, List<string> requests)
    {
        return new BilibiliPlaylistResolver((url, token) =>
        {
            requests.Add(url);
            Require(url.Contains(season ? "seasons_archives_list" : "/x/series/"), "wrong API namespace");
            if (url.Contains("/x/series/series?")) return Task.FromResult(Metadata(total));
            int page = QueryPage(url), first = (page - 1) * 30 + 1;
            return Task.FromResult(Page(season, page, total, 30, first, Math.Max(0, Math.Min(30, total - first + 1))).ToString());
        });
    }

    private static BilibiliPlaylistResolver Transform(int total, Action<JObject, int> transform)
    {
        return new BilibiliPlaylistResolver((url, token) =>
        {
            if (url.Contains("/x/series/series?")) return Task.FromResult(Metadata(total));
            int page = QueryPage(url), first = (page - 1) * 30 + 1;
            JObject response = Page(false, page, total, 30, first, Math.Max(0, Math.Min(30, total - first + 1)));
            transform((JObject)response["data"], page);
            return Task.FromResult(response.ToString());
        });
    }

    private static string Metadata(int count)
    {
        return new JObject { ["code"] = 0, ["data"] = new JObject { ["meta"] = new JObject { ["mid"] = 161775300, ["series_id"] = 1774529, ["name"] = "回归测试列表", ["total"] = count } } }.ToString();
    }

    private static JObject Page(bool season, int page, int total, int pageSize, int first, int count)
    {
        var archives = new JArray();
        for (int index = first; index < first + count; index++)
            archives.Add(new JObject { ["bvid"] = "BV" + index.ToString("D10"), ["aid"] = 100000 + index, ["title"] = "视频 " + index, ["duration"] = 100 + index });
        return new JObject
        {
            ["code"] = 0,
            ["data"] = new JObject
            {
                ["page"] = new JObject { [season ? "page_num" : "num"] = page, [season ? "page_size" : "size"] = pageSize, ["total"] = total },
                ["meta"] = new JObject { ["mid"] = 161775300, [season ? "season_id" : "series_id"] = 1774529, ["name"] = "回归测试列表", ["total"] = total },
                ["archives"] = archives
            }
        };
    }

    private static int QueryPage(string url)
    {
        var query = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
        return int.Parse(query.ContainsKey("pn") ? query["pn"] : query["page_num"]);
    }

    private static async Task Reject(BilibiliPlaylistResolver resolver, string url, string expected)
    {
        try { await resolver.ResolveAsync(url, CancellationToken.None); }
        catch (InvalidOperationException error) { Require(error.Message.Contains(expected), "unexpected error: " + error.Message); return; }
        throw new Exception("incomplete or blocked playlist was accepted");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Check(string name, Func<Task> test)
    {
        try { test().GetAwaiter().GetResult(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
}
