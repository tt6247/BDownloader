using BDownloader.Core;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// Opt-in network verification. Uses public videos with no Cookie file, browser
// account, or saved application session. The complete sample is downloaded.
internal static class LiveDownloadTests
{
    private const string YouTubeUrl = "https://www.youtube.com/watch?v=OfZiLFe2Efs";
    private const string BilibiliListUrl = "https://www.bilibili.com/list/161775300/?sid=1774529&spm_id_from=333.1387.0.0&oid=1956380285&bvid=BV1Ky411q7QC";
    private const string BilibiliVideoUrl = "https://www.bilibili.com/video/BV1Ky411q7QC";
    private static int failures;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        try { RunAsync(args).GetAwaiter().GetResult(); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL unexpected: " + Sanitize(error.Message)); }
        Console.WriteLine("RESULT failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Expected application directory and artifact directory.");
        string appDirectory = Path.GetFullPath(args[0]);
        string artifactDirectory = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(artifactDirectory);
        Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        var engine = new MediaEngine(appDirectory);
        Console.WriteLine("LIVE started UTC=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Console.WriteLine("LIVE public sessions only; no imported cookies; sample duration is unrestricted");

        MediaEntry biliEntry = null;

        {
            await Check("Bilibili playlist resolves all pages with unique videos", async () =>
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                {
                    var resolver = new BilibiliPlaylistResolver(FetchPublicAsync);
                    PlaylistResult list = await resolver.ResolveAsync(BilibiliListUrl, timeout.Token);
                    Require(list.IsComplete, "Resolver did not confirm the complete list.");
                    Require(list.ExpectedCount > 1 && list.Entries.Count == list.ExpectedCount, "Playlist count differs from the declared total.");
                    Require(list.Entries.Select(x => x.Id).Distinct().Count() == list.ExpectedCount, "Duplicate playlist IDs.");
                    Console.WriteLine("BILIBILI title=" + Sanitize(list.Title) + " expected=" + list.ExpectedCount + " actual=" + list.Entries.Count);
                    Console.WriteLine("BILIBILI first=" + list.Entries.First().Id + " last=" + list.Entries.Last().Id);
                }
            });
            // Keep a canonical public entry so DownloadAsync is exercised even
            // if the metadata-only request encounters Bilibili risk control.
            biliEntry = new MediaEntry { Id = "BV1Ky411q7QC", Title = "Bilibili public representative", Url = BilibiliVideoUrl };
            await Check("Bilibili representative resolves through the application engine", async () =>
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                {
                    MediaCollection collection = await engine.InspectAsync(BilibiliVideoUrl, null, timeout.Token);
                    Require(collection.Entries.Count > 0, "No representative video was resolved.");
                    biliEntry = collection.Entries.First();
                    Console.WriteLine("BILIBILI representative=" + biliEntry.Id + " parts=" + collection.Entries.Count
                        + " duration=" + (biliEntry.DurationSeconds?.ToString("0.###", CultureInfo.InvariantCulture) ?? "from download metadata"));
                    Console.WriteLine("BILIBILI video title=" + Sanitize(biliEntry.Title));
                }
            });
        }

        MediaEntry youtubeEntry = null;
        await Check("YouTube supplied URL resolves through the application engine", async () =>
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
            {
                MediaCollection collection = await engine.InspectAsync(YouTubeUrl, null, timeout.Token);
                Require(collection.ExpectedEntryCount == 1 && collection.Entries.Count == 1, "Expected a single video.");
                youtubeEntry = collection.Entries.Single();
                Require(youtubeEntry.Id == "OfZiLFe2Efs", "Resolved a different video.");
                Require(youtubeEntry.DurationSeconds > 0, "No expected source duration.");
                Console.WriteLine("YOUTUBE id=" + youtubeEntry.Id + " duration=" + youtubeEntry.DurationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture));
                Console.WriteLine("YOUTUBE title=" + Sanitize(youtubeEntry.Title));
            }
        });
        if (args.Contains("--inspect-only")) return;

        var samples = new List<Tuple<string, MediaEntry, MediaDownloadMode>>();
        if (youtubeEntry != null)
        {
            samples.Add(Tuple.Create("YouTube", youtubeEntry, MediaDownloadMode.VideoAndAudio));
            samples.Add(Tuple.Create("YouTube", youtubeEntry, MediaDownloadMode.AudioM4a));
        }
        if (biliEntry != null) samples.Add(Tuple.Create("Bilibili", biliEntry, MediaDownloadMode.VideoAndAudio));
        foreach (var sample in samples)
        {
            MediaEntry entry = sample.Item2;
            MediaDownloadMode mode = sample.Item3;
            await Check("complete " + sample.Item1 + " " + mode + " download passes engine verification", async () =>
            {
                string outputDirectory = Path.Combine(artifactDirectory, sample.Item1, mode.ToString());
                Directory.CreateDirectory(outputDirectory);
                var options = new MediaDownloadOptions { OutputDirectory = outputDirectory, Mode = mode, MaximumHeight = 360, Session = null };
                var progress = new SafeProgress(sample.Item1 + "/" + mode);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
                {
                    IList<MediaDownloadResult> results = await engine.DownloadAsync(new[] { entry }, options, progress, timeout.Token);
                    Require(results.Count == 1, "Expected one download result.");
                    MediaDownloadResult result = results.Single();
                    Require(result.Success && !result.Cancelled, result.Error ?? "Download was not successful.");
                    Require(progress.SawVerification && progress.SawCompleted, "Download bypassed the verification/completion states.");
                    Require(File.Exists(result.FilePath) && File.Exists(result.ReceiptPath), "Missing final media or verification receipt.");
                    Require(!Directory.EnumerateFiles(outputDirectory, "*.part", SearchOption.AllDirectories).Any(), "Incomplete transfer fragments remain.");
                    JObject receipt = JObject.Parse(File.ReadAllText(result.ReceiptPath));
                    Require((string)receipt["MediaId"] == entry.Id, "Receipt media ID mismatch.");
                    Require((string)receipt["Mode"] == mode.ToString(), "Receipt mode mismatch.");
                    Require((bool)receipt["HasAudio"], "Output has no audio track.");
                    Require((bool)receipt["HasVideo"] == (mode == MediaDownloadMode.VideoAndAudio), "Output video-track presence is wrong.");
                    Require(mode != MediaDownloadMode.VideoAndAudio || (int)receipt["Height"] <= 360, "Height exceeds requested limit.");
                    double expectedDuration = entry.DurationSeconds ?? (double?)receipt["ExpectedDurationSeconds"] ?? 0;
                    Require(expectedDuration > 0, "Source duration was unavailable during inspection and download.");
                    Require((double)receipt["DecodedSeconds"] >= expectedDuration - 2.0, "Full decode did not reach expected duration.");
                    Require((long)receipt["FileSize"] == new FileInfo(result.FilePath).Length, "Receipt size mismatch.");
                    string hash;
                    using (SHA256 sha = SHA256.Create())
                    using (FileStream stream = File.OpenRead(result.FilePath))
                        hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                    Require(hash == result.Sha256 && hash == (string)receipt["Sha256"], "Independent SHA256 differs from the receipt.");
                    string receiptText = receipt.ToString();
                    Require(!Regex.IsMatch(receiptText, @"(?i)(googlevideo\.com|SESSDATA|bili_jct|signature=|cookie:)"), "Receipt contains private transfer URLs or account material.");
                    Console.WriteLine("VERIFIED source=" + sample.Item1 + " mode=" + mode + " bytes=" + new FileInfo(result.FilePath).Length
                        + " expected=" + expectedDuration.ToString("0.###", CultureInfo.InvariantCulture)
                        + " actual=" + result.ActualDurationSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                        + " decoded=" + ((double)receipt["DecodedSeconds"]).ToString("0.###", CultureInfo.InvariantCulture)
                        + " height=" + (int)receipt["Height"] + " sha256=" + hash);
                    Console.WriteLine("ARTIFACT " + Path.GetFileName(result.FilePath));
                    Console.WriteLine("RECEIPT " + Path.GetFileName(result.ReceiptPath));
                }
            });
        }
    }

    private static async Task Check(string name, Func<Task> test)
    {
        try { await test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + Sanitize(error.Message)); }
    }

    private static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidOperationException(error);
    }

    private static string Sanitize(string message)
    {
        string text = Regex.Replace(message ?? "", @"https?://[^\s]+", "[URL omitted]");
        text = Regex.Replace(text, @"(?i)(SESSDATA|bili_jct|authorization|cookie|token|signature|sig|key)\s*[:=]\s*[^\s;,&]+", "$1=[redacted]");
        return text.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static async Task<string> FetchPublicAsync(string url, CancellationToken token)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.UserAgent = "Mozilla/5.0";
        request.Referer = "https://www.bilibili.com/";
        request.Timeout = 30000;
        request.ReadWriteTimeout = 30000;
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        using (token.Register(request.Abort))
        {
            try
            {
                using (WebResponse response = await request.GetResponseAsync())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return await reader.ReadToEndAsync();
            }
            catch (WebException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        }
    }

    private sealed class SafeProgress : IProgress<MediaProgress>
    {
        private readonly object sync = new object();
        private readonly string mode;
        private MediaDownloadState? previous;
        private int previousPercent = -20;
        public bool SawVerification { get; private set; }
        public bool SawCompleted { get; private set; }
        public SafeProgress(string mode) { this.mode = mode; }
        public void Report(MediaProgress value)
        {
            lock (sync)
            {
                if (value.State == MediaDownloadState.Verifying) SawVerification = true;
                if (value.State == MediaDownloadState.Completed) SawCompleted = true;
                int percent = (int)value.Percent;
                if (previous != value.State || percent >= previousPercent + 20)
                {
                    Console.WriteLine("PROGRESS mode=" + mode + " state=" + value.State + " percent=" + percent);
                    previousPercent = percent;
                    previous = value.State;
                }
            }
        }
    }
}
