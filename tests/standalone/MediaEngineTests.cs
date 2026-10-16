using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BDownloader.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Offline integration/regression runner. Takes application runtime directory and artifact directory.</summary>
internal static class MediaEngineTests
{
    private static int failures;
    private static string artifacts;
    private static string ffmpeg;
    private static MediaEngine engine;

    private static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--test-worker")
        {
            File.WriteAllText(Environment.GetEnvironmentVariable("BD_TEST_CHILD_PID"), Process.GetCurrentProcess().Id.ToString());
            Thread.Sleep(60000); return 0;
        }
        if (args.Contains("--ignore-config"))
        {
            string fixture = Environment.GetEnvironmentVariable("BD_TEST_FAKE_MEDIA");
            if (args.Contains("--no-playlist") && !string.IsNullOrEmpty(fixture))
            {
                string destination = args[Array.IndexOf(args, "--paths") + 1];
                string template = args[Array.IndexOf(args, "--output") + 1];
                string file = Path.Combine(destination, template.Replace("%(title).100B", "Synthetic").Replace("%(id)s", "fixture").Replace("%(ext)s", "mp4"));
                Directory.CreateDirectory(destination); if (!File.Exists(file)) File.Copy(fixture, file);
                Console.Error.WriteLine("__BD_PROGRESS__{\"downloaded_bytes\":50,\"total_bytes\":100,\"speed\":1000}");
                Console.WriteLine("__BD_DURATION__3");
                Console.WriteLine("__BD_FILE__" + JsonConvert.SerializeObject(file));
                return 0;
            }
            using (Process child = Process.Start(new ProcessStartInfo { FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = "--test-worker", UseShellExecute = false, CreateNoWindow = true }))
                Thread.Sleep(60000);
            return 0;
        }
        if (args.Length < 2) { Console.Error.WriteLine("Usage: MediaEngineTests.exe <runtime-directory> <artifact-directory>"); return 2; }
        artifacts = Path.GetFullPath(args[1]); Directory.CreateDirectory(artifacts);
        ffmpeg = Path.Combine(Path.GetFullPath(args[0]), "ffmpeg.exe");
        engine = new MediaEngine(Path.GetFullPath(args[0]));

        Check("metadata keeps full playlist and detects missing entries", () => {
            var data = JObject.Parse("{ 'title':'List', 'playlist_count':3, 'extractor_key':'YoutubeTab', 'entries':[ {'id':'a','url':'a','title':'First','ie_key':'Youtube','duration':3}, null, {'id':'b','url':'b','title':'Second','ie_key':'Youtube'} ] }");
            MediaCollection list = MediaEngine.ParseCollection(data, "https://www.youtube.com/playlist?list=example");
            Require(list.Entries.Count == 2 && list.ExpectedEntryCount == 3, "playlist count mismatch was hidden");
            Require(list.Entries[1].Url == "https://www.youtube.com/watch?v=b", "flat entry URL");
            Require(list.Warnings.Count > 0, "unavailable entry needs warning");
        });
        Check("unsupported hosts and user-info URLs rejected", () => {
            Require(MediaEngine.IsSupportedUrl("https://www.youtube.com/watch?v=abc"), "YouTube rejected");
            Require(MediaEngine.IsSupportedUrl("https://www.bilibili.com/video/BV1abc"), "Bili rejected");
            Require(!MediaEngine.IsSupportedUrl("https://youtube.com.attacker.example/video"), "spoofed host allowed");
            Require(!MediaEngine.IsSupportedUrl("https://user:secret@youtube.com/video"), "credentials in URL allowed");
            Require(!MediaEngine.IsSupportedUrl("file:///C:/private.mp4"), "local URL allowed");
        });
        Check("quality limit covers fallback and audio mode switches", () => {
            var arguments = MediaEngine.FormatArguments(new MediaDownloadOptions { MaximumHeight = 720 });
            Require(arguments[1] == "bv*[height<=720]+ba/b[height<=720]", "quality fallback exceeds ceiling");
            var audio = MediaEngine.FormatArguments(new MediaDownloadOptions { Mode = MediaDownloadMode.AudioMp3 });
            Require(audio.Contains("mp3") && audio.Contains("--extract-audio"), "audio conversion missing");
        });
        Check("Windows escaping handles spaces quotes and trailing slashes", () => {
            Type process = typeof(MediaEngine).Assembly.GetType("BDownloader.Core.MediaProcess");
            var quote = process.GetMethod("QuoteArgument", BindingFlags.Static | BindingFlags.Public);
            Require((string)quote.Invoke(null, new object[] { "C:\\some dir\\" }) == "\"C:\\some dir\\\\\"", "trailing slash quoting");
            Require((string)quote.Invoke(null, new object[] { "a\"b" }) == "\"a\\\"b\"", "embedded quote escaping");
        });
        Check("AV1 library notice is allowed while decoder errors remain fatal", () => {
            var check = typeof(MediaEngine).GetMethod("HasDecodeErrors", BindingFlags.NonPublic | BindingFlags.Static);
            string notice = "[libdav1d @ 05848cc0] Frame size limit reduced from 2147483647 to 67108864.\r\n";
            Require(!(bool)check.Invoke(null, new object[] { notice }), "AV1 informational notice rejected");
            Require((bool)check.Invoke(null, new object[] { notice + "[aac @ 1234] Input buffer exhausted before END element found" }), "decode error ignored");
            Require((bool)check.Invoke(null, new object[] { "[libdav1d @ 05848cc0] Error parsing frame" }), "AV1 corruption ignored");
            Require(!(bool)check.Invoke(null, new object[] { notice + "[libdav1d @ 075c3d80] Unknown Metadata OBU type 6\r\n    Last message repeated 41 times\r\n" }), "optional AV1 metadata notice rejected");
            Require((bool)check.Invoke(null, new object[] { "Last message repeated 41 times" }), "unattributed repeated errors ignored");
            Require((bool)check.Invoke(null, new object[] { "[libdav1d @ 075c3d80] Unknown Metadata OBU type 7" }), "unrecognized metadata error ignored");
        });

        string video = Path.Combine(artifacts, "complete.mp4");
        string silent = Path.Combine(artifacts, "silent.mp4");
        string audioFile = Path.Combine(artifacts, "audio.m4a");
        Check("create tiny deterministic real media fixtures", () => {
            Execute(ffmpeg, new[] { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=12", "-f", "lavfi", "-i", "sine=frequency=500:sample_rate=44100", "-t", "3", "-c:v", "mpeg4", "-c:a", "aac", video });
            Execute(ffmpeg, new[] { "-v", "error", "-y", "-i", video, "-an", "-c:v", "copy", silent });
            Execute(ffmpeg, new[] { "-v", "error", "-y", "-i", video, "-vn", "-c:a", "copy", audioFile });
        });
        Check("full decode duration streams and SHA-256 receipt pass", () => {
            MediaDownloadResult result = Verify(video, 3, MediaDownloadMode.VideoAndAudio);
            Require(result.Success, result.Error);
            JObject proof = JObject.Parse(File.ReadAllText(result.ReceiptPath));
            Require(result.Sha256.Length == 64 && (string)proof["Sha256"] == result.Sha256, "hash receipt missing");
            Require((bool)proof["HasAudio"] && (bool)proof["HasVideo"], "stream receipt missing");
            Require(Math.Abs(result.ActualDurationSeconds - 3) < 0.2, "duration mismatch");
            Require(!File.ReadAllText(result.ReceiptPath).Contains("secret"), "receipt leaked private URL query");
            Require(!File.Exists(video + ".verification.json"), "successful verification left a media sidecar");
            Require(result.ReceiptPath.StartsWith(Path.Combine(BDownloader.Services.AppPaths.DataDirectory, "Storage", "Verification") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "receipt is outside app data");
        });
        Check("video only and audio only modes verify actual tracks", () => {
            var verified = Verify(silent, 3, MediaDownloadMode.VideoOnly);
            Require(verified.Success, "silent video failed");
            Require(Verify(audioFile, 3, MediaDownloadMode.AudioM4a).Success, "audio file failed");
            Require(!Verify(silent, 3, MediaDownloadMode.VideoAndAudio).Success, "missing audio accepted");
            Require(File.Exists(verified.ReceiptPath), "failed recheck removed the historical baseline");
        });
        Check("recheck detects changed but still decodable media", () => {
            string changed = Path.Combine(artifacts, "changed.mp4");
            string remuxed = Path.Combine(artifacts, "changed-remux.mp4");
            File.Copy(video, changed, true);
            var initial = Verify(changed, 3, MediaDownloadMode.VideoAndAudio);
            Require(initial.Success, "initial changed fixture did not verify");
            string baseline = File.ReadAllText(initial.ReceiptPath);
            Execute(ffmpeg, new[] { "-v", "error", "-y", "-i", changed, "-c", "copy", "-metadata", "title=modified after verification", remuxed });
            File.Delete(changed); File.Move(remuxed, changed);
            var result = Verify(changed, 3, MediaDownloadMode.VideoAndAudio);
            Require(!result.Success && result.Error.Contains("SHA-256"), "changed decodable file accepted");
            Require(File.ReadAllText(initial.ReceiptPath) == baseline, "mismatched historical baseline overwritten");
            var again = Verify(changed, 3, MediaDownloadMode.VideoAndAudio);
            Require(!again.Success && again.Error.Contains("SHA-256"), "second recheck accepted changed bytes after losing baseline");
            Require(File.ReadAllText(initial.ReceiptPath) == baseline, "second failed recheck changed baseline");
        });
        Check("duration mismatch and resolution violation rejected", () => {
            Require(!Verify(video, 100, MediaDownloadMode.VideoAndAudio).Success, "short file accepted");
            var result = engine.VerifyAsync(video, Entry(3), new MediaDownloadOptions { MaximumHeight = 80 }, CancellationToken.None).GetAwaiter().GetResult();
            Require(!result.Success, "over-height file accepted");
        });
        Check("truncated media cannot receive a completion receipt", () => {
            string damaged = Path.Combine(artifacts, "truncated.mp4");
            byte[] source = File.ReadAllBytes(video); File.WriteAllBytes(damaged, source.Take(source.Length / 2).ToArray());
            MediaDownloadResult result = Verify(damaged, 3, MediaDownloadMode.VideoAndAudio);
            Require(!result.Success && !File.Exists(damaged + ".verification.json"), "truncated file verified");
        });
        Check("cancelled verification cannot mark success", () => {
            string receipt = Verify(video, 3, MediaDownloadMode.VideoAndAudio).ReceiptPath;
            string baseline = File.ReadAllText(receipt);
            var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            MediaDownloadResult result = engine.VerifyAsync(video, Entry(3), new MediaDownloadOptions(), cancellation.Token).GetAwaiter().GetResult();
            Require(result.Cancelled && !result.Success, "cancelled check accepted");
            Require(File.ReadAllText(receipt) == baseline, "cancelled recheck changed the historical baseline");
        });
        Check("legacy sidecar migrates only after successful verification", () => {
            string legacy = Path.Combine(artifacts, "legacy.mp4"); File.Copy(video, legacy);
            var initial = Verify(legacy, 3, MediaDownloadMode.VideoAndAudio);
            Require(initial.Success, initial.Error);
            string baseline = File.ReadAllText(initial.ReceiptPath);
            File.Move(initial.ReceiptPath, legacy + ".verification.json");
            File.WriteAllText(legacy + ".notes.json", "keep unrelated metadata");
            var migrated = Verify(legacy, 3, MediaDownloadMode.VideoAndAudio);
            Require(migrated.Success, migrated.Error);
            Require(!File.Exists(legacy + ".verification.json") && File.Exists(migrated.ReceiptPath), "legacy sidecar not migrated");
            Require((string)JObject.Parse(baseline)["Sha256"] == migrated.Sha256, "migration changed baseline hash");
            Require(File.Exists(legacy + ".notes.json"), "unrelated JSON deleted");
        });
        Check("failed and cancelled legacy rechecks preserve their baseline", () => {
            string legacy = Path.Combine(artifacts, "legacy-failure.mp4"); File.Copy(video, legacy);
            var initial = Verify(legacy, 3, MediaDownloadMode.VideoAndAudio);
            Require(initial.Success, initial.Error);
            File.Move(initial.ReceiptPath, legacy + ".verification.json");
            string baseline = File.ReadAllText(legacy + ".verification.json");
            var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cancelled = engine.VerifyAsync(legacy, Entry(3), new MediaDownloadOptions(), cancellation.Token).GetAwaiter().GetResult();
            Require(cancelled.Cancelled && File.ReadAllText(legacy + ".verification.json") == baseline, "cancel lost legacy baseline");
            string remuxed = Path.Combine(artifacts, "legacy-remux.mp4");
            Execute(ffmpeg, new[] { "-v", "error", "-y", "-i", legacy, "-c", "copy", "-metadata", "title=changed", remuxed });
            File.Delete(legacy); File.Move(remuxed, legacy);
            var failed = Verify(legacy, 3, MediaDownloadMode.VideoAndAudio);
            Require(!failed.Success && failed.Error.Contains("SHA-256"), "legacy hash mismatch accepted");
            Require(File.ReadAllText(legacy + ".verification.json") == baseline && !File.Exists(initial.ReceiptPath), "failure replaced legacy baseline");
        });
        Check("same media filename in different folders has independent baselines", () => {
            string first = Path.Combine(artifacts, "folder-a", "same.mp4");
            string second = Path.Combine(artifacts, "folder-b", "same.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(first)); Directory.CreateDirectory(Path.GetDirectoryName(second));
            File.Copy(video, first); File.Copy(silent, second);
            var a = Verify(first, 3, MediaDownloadMode.VideoAndAudio);
            var b = Verify(second, 3, MediaDownloadMode.VideoOnly);
            Require(a.Success && b.Success && a.ReceiptPath != b.ReceiptPath, "same filenames share a baseline");
            Require(Verify(first, 3, MediaDownloadMode.VideoAndAudio).Success && Verify(second, 3, MediaDownloadMode.VideoOnly).Success, "independent recheck failed");
        });
        Check("download reports stderr progress quarantines corruption and retries fresh", () => TestFakeDownload(video).GetAwaiter().GetResult());
        Check("metadata cancellation terminates child process tree", () => TestCancellation().GetAwaiter().GetResult());
        Console.WriteLine("Media engine failures: " + failures); return failures == 0 ? 0 : 1;
    }

    private static MediaEntry Entry(double duration) => new MediaEntry { Id = "test", Title = "Test", Url = "https://www.youtube.com/watch?v=fixture&token=secret", DurationSeconds = duration };
    private static MediaDownloadResult Verify(string file, double duration, MediaDownloadMode mode) =>
        engine.VerifyAsync(file, Entry(duration), new MediaDownloadOptions { Mode = mode }, CancellationToken.None).GetAwaiter().GetResult();

    private static void Execute(string executable, IEnumerable<string> arguments)
    {
        var quote = typeof(MediaEngine).Assembly.GetType("BDownloader.Core.MediaProcess").GetMethod("QuoteArgument", BindingFlags.Public | BindingFlags.Static);
        using (var process = Process.Start(new ProcessStartInfo { FileName = executable,
            Arguments = string.Join(" ", arguments.Select(x => (string)quote.Invoke(null, new object[] { x }))),
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true }))
        { string error = process.StandardError.ReadToEnd(); process.WaitForExit(); Require(process.ExitCode == 0, error); }
    }

    private static async Task TestCancellation()
    {
        string fake = PrepareFakeRuntime();
        string pidFile = Path.Combine(fake, "worker.pid"); if (File.Exists(pidFile)) File.Delete(pidFile);
        Environment.SetEnvironmentVariable("BD_TEST_CHILD_PID", pidFile);
        using (var cancellation = new CancellationTokenSource())
        {
            Task<MediaCollection> running = new MediaEngine(fake).InspectAsync("https://www.youtube.com/watch?v=fixture", null, cancellation.Token);
            for (int attempt = 0; attempt < 50 && !File.Exists(pidFile); attempt++) await Task.Delay(100);
            cancellation.Cancel();
            bool cancelled = false;
            try { await running; } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "metadata operation did not cancel"); Require(File.Exists(pidFile), "test descendant never started");
            int pid = int.Parse(File.ReadAllText(pidFile));
            bool alive;
            try { using (Process child = Process.GetProcessById(pid)) { child.WaitForExit(3000); alive = !child.HasExited; } }
            catch (ArgumentException) { alive = false; }
            Require(!alive, "descendant process survived cancellation");
        }
        Environment.SetEnvironmentVariable("BD_TEST_CHILD_PID", null);
    }

    private static string PrepareFakeRuntime()
    {
        string fake = Path.Combine(artifacts, "fake-runtime"); Directory.CreateDirectory(fake);
        string self = Assembly.GetExecutingAssembly().Location;
        File.Copy(self, Path.Combine(fake, "yt-dlp.exe"), true);
        foreach (string assembly in new[] { typeof(MediaEngine).Assembly.Location, typeof(JObject).Assembly.Location })
            if (!string.Equals(Path.GetFileName(assembly), Path.GetFileName(self), StringComparison.OrdinalIgnoreCase))
                File.Copy(assembly, Path.Combine(fake, Path.GetFileName(assembly)), true);
        return fake;
    }

    private static async Task TestFakeDownload(string fixture)
    {
        string fake = PrepareFakeRuntime();
        File.Copy(ffmpeg, Path.Combine(fake, "ffmpeg.exe"), true);
        string destination = Path.Combine(artifacts, "fake-download-" + Guid.NewGuid().ToString("N"));
        var options = new MediaDownloadOptions { OutputDirectory = destination, MaximumHeight = 90 };
        var item = Entry(0); item.DurationSeconds = null;
        var progress = new RecordedProgress();
        Environment.SetEnvironmentVariable("BD_TEST_FAKE_MEDIA", fixture);
        try
        {
            var fakeEngine = new MediaEngine(fake);
            MediaDownloadResult first = (await fakeEngine.DownloadAsync(new[] { item }, options, progress, CancellationToken.None))[0];
            Require(first.Success, first.Error);
            Require(first.FilePath.Contains("_90p"), "quality missing from output filename");
            Require(item.DurationSeconds == 3, "discovered duration was not persisted");
            Require(progress.Events.Any(x => x.State == MediaDownloadState.Downloading && x.Percent == 50), "stderr progress not reported");
            Require(progress.Events.FindIndex(x => x.State == MediaDownloadState.Completed) > progress.Events.FindIndex(x => x.State == MediaDownloadState.Verifying), "completion came before full verification");
            byte[] bytes = File.ReadAllBytes(first.FilePath); File.WriteAllBytes(first.FilePath, bytes.Take(bytes.Length / 2).ToArray());
            MediaDownloadResult bad = (await fakeEngine.DownloadAsync(new[] { item }, options, null, CancellationToken.None))[0];
            Require(!bad.Success && bad.FilePath.Contains(".failed-") && File.Exists(bad.FilePath), "corrupt final was not quarantined");
            Require(!File.Exists(first.FilePath), "bad final blocks a fresh retry");
            MediaDownloadResult retry = (await fakeEngine.DownloadAsync(new[] { item }, options, null, CancellationToken.None))[0];
            Require(retry.Success && File.Exists(retry.ReceiptPath), "retry did not redownload and verify");
            Require(!Directory.GetFiles(destination, "*.verification.json").Any(), "completed download left a sidecar");
        }
        finally { Environment.SetEnvironmentVariable("BD_TEST_FAKE_MEDIA", null); }
    }

    private sealed class RecordedProgress : IProgress<MediaProgress>
    {
        public readonly List<MediaProgress> Events = new List<MediaProgress>();
        public void Report(MediaProgress value) { lock (Events) Events.Add(value); }
    }
}
