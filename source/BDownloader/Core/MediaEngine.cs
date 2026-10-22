using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BDownloader.Core
{
    public enum MediaDownloadMode { VideoAndAudio, VideoOnly, AudioM4a, AudioMp3 }
    public enum MediaDownloadState { Resolving, Downloading, Processing, Verifying, Completed, Failed, Cancelled }

    public sealed class MediaSession
    {
        public string CookiesFile { get; set; }
        public string BrowserCookies { get; set; }
    }

    public sealed class MediaEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public double? DurationSeconds { get; set; }
        public int PlaylistIndex { get; set; }
        public bool IsSelected { get; set; } = true;
    }

    public sealed class MediaCollection
    {
        public string Title { get; set; }
        public string SourceUrl { get; set; }
        public IList<MediaEntry> Entries { get; set; } = new List<MediaEntry>();
        public int ExpectedEntryCount { get; set; }
        /// <summary>False when the extractor did not report a list total, so completeness cannot be proven.</summary>
        public bool CountKnown { get; set; }
        public IList<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class MediaDownloadOptions
    {
        public string OutputDirectory { get; set; }
        public MediaDownloadMode Mode { get; set; } = MediaDownloadMode.VideoAndAudio;
        public int MaximumHeight { get; set; }
        public MediaSession Session { get; set; }
    }

    public sealed class MediaProgress
    {
        public MediaEntry Entry { get; set; }
        public MediaDownloadState State { get; set; }
        public double Percent { get; set; }
        public string Message { get; set; }
        public int CompletedCount { get; set; }
        public int TotalCount { get; set; }
        public double? BytesPerSecond { get; set; }
        public long? DownloadedBytes { get; set; }
        public long? TotalBytes { get; set; }
    }

    public sealed class MediaDownloadResult
    {
        public MediaEntry Entry { get; set; }
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public string FilePath { get; set; }
        public string ReceiptPath { get; set; }
        public string Error { get; set; }
        public string Sha256 { get; set; }
        public double ActualDurationSeconds { get; set; }
    }

    /// <summary>
    /// Starts bundled executables directly (never through a shell). A transfer is only complete
    /// after stream/duration checks, a full decode and a SHA-256 receipt have all succeeded.
    /// Login belongs to the caller; only a cookie file or browser name is passed to yt-dlp.
    /// </summary>
    public sealed class MediaEngine
    {
        private readonly string directory;
        private string YtDlp => Path.Combine(directory, "yt-dlp.exe");
        private string FFmpeg => Path.Combine(directory, "ffmpeg.exe");
        private const string ProgressPrefix = "__BD_PROGRESS__";
        private const string FilePrefix = "__BD_FILE__";
        private const string DurationPrefix = "__BD_DURATION__";

        public MediaEngine(string appDirectory)
        {
            if (string.IsNullOrWhiteSpace(appDirectory)) throw new ArgumentNullException(nameof(appDirectory));
            directory = Path.GetFullPath(appDirectory);
        }

        public async Task<MediaCollection> InspectAsync(string url, MediaSession session, CancellationToken token)
        {
            ValidateUrl(url);
            EnsureExecutable(YtDlp);
            var arguments = BaseArguments(session);
            arguments.AddRange(new[] { "--flat-playlist", "--dump-single-json", "--yes-playlist", "--", url });
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                ProcessOutput process;
                try { process = await MediaProcess.RunAsync(YtDlp, arguments, directory, null, true, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { throw new TimeoutException("解析超时，请检查网络或登录状态后重试。"); }
                if (process.ExitCode != 0) throw new InvalidOperationException(FriendlyError(process.StandardError, session));
                JObject document;
                try { document = JObject.Parse(process.StandardOutput); }
                catch (JsonException) { throw new InvalidOperationException("解析器未返回有效的视频信息。"); }
                return ParseCollection(document, url);
            }
        }

        /// <summary>Parses extractor JSON. Public for deterministic offline regression tests.</summary>
        public static MediaCollection ParseCollection(JObject document, string sourceUrl)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var collection = new MediaCollection { Title = (string)document["title"] ?? "视频", SourceUrl = sourceUrl };
            var entries = document["entries"] as JArray;
            var nodes = entries == null ? new[] { document } : entries.OfType<JObject>().ToArray();
            int index = 0;
            foreach (JObject node in nodes)
            {
                index++;
                string id = (string)node["id"];
                string url = (string)node["webpage_url"] ?? (string)node["original_url"] ?? (string)node["url"];
                if (!IsSupportedUrl(url))
                {
                    string extractor = ((string)node["ie_key"] ?? (string)node["extractor_key"] ?? (string)document["extractor_key"] ?? "").ToLowerInvariant();
                    if (extractor.Contains("youtube") && !string.IsNullOrEmpty(id)) url = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id);
                    else if (!string.IsNullOrEmpty(id) && id.StartsWith("BV", StringComparison.Ordinal)) url = "https://www.bilibili.com/video/" + Uri.EscapeDataString(id);
                    else if (entries == null) url = sourceUrl;
                }
                if (!IsSupportedUrl(url))
                {
                    collection.Warnings.Add("第 " + index + " 项无有效链接，可能已删除或无访问权限。");
                    continue;
                }
                string availability = (string)node["availability"];
                if (availability == "private" || availability == "premium_only" || availability == "subscriber_only")
                    collection.Warnings.Add("“" + ((string)node["title"] ?? id) + "”需要相应账号权限。");
                collection.Entries.Add(new MediaEntry {
                    Id = id ?? index.ToString(CultureInfo.InvariantCulture),
                    Title = (string)node["title"] ?? id ?? "视频",
                    Url = url,
                    PlaylistIndex = (int?)node["playlist_index"] ?? index,
                    DurationSeconds = PositiveNumber(node["duration"])
                });
            }
            int? declaredCount = (int?)document["playlist_count"] ?? (int?)document["n_entries"];
            collection.ExpectedEntryCount = Math.Max(entries?.Count ?? 1, declaredCount ?? 0);
            // Flat extractor output often omits the total; then "entries counted" is not proof of completeness.
            collection.CountKnown = entries == null || declaredCount.HasValue;
            if (!collection.CountKnown)
                collection.Warnings.Add("解析器未提供列表总数，无法确认是否取得全部视频。");
            if (collection.Entries.Count != collection.ExpectedEntryCount)
                collection.Warnings.Add("列表声明 " + collection.ExpectedEntryCount + " 项，取得 " + collection.Entries.Count + " 项；请检查不可用条目。");
            if (collection.Entries.Count == 0) throw new InvalidOperationException("没有找到可下载的视频。请检查链接或登录状态。");
            return collection;
        }

        public async Task<IList<MediaDownloadResult>> DownloadAsync(IEnumerable<MediaEntry> entries, MediaDownloadOptions options,
            IProgress<MediaProgress> progress, CancellationToken token)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            ValidateOptions(options);
            EnsureExecutable(YtDlp);
            EnsureExecutable(FFmpeg);
            var selected = entries.Where(x => x != null && x.IsSelected).ToList();
            var results = new List<MediaDownloadResult>();
            int completed = 0;
            foreach (MediaEntry entry in selected)
            {
                if (token.IsCancellationRequested)
                {
                    results.Add(new MediaDownloadResult { Entry = entry, Cancelled = true, Error = "已取消，未完成的文件可在下次继续下载。" });
                    Report(progress, entry, MediaDownloadState.Cancelled, 0, "已取消", completed, selected.Count);
                    continue;
                }
                string path = null;
                try
                {
                    ValidateUrl(entry.Url);
                    var args = BaseArguments(options.Session);
                    args.AddRange(new[] { "--no-playlist", "--continue", "--part", "--abort-on-unavailable-fragments",
                        "--retries", "10", "--fragment-retries", "10", "--file-access-retries", "3", "--newline", "--progress", "--no-simulate",
                        "--windows-filenames", "--trim-filenames", "180", "--no-overwrites", "--ffmpeg-location", directory,
                        "--paths", Path.GetFullPath(options.OutputDirectory), "--output", OutputTemplate(entry, options),
                        "--progress-template", "download:" + ProgressPrefix + "%(progress)j",
                        "--print", "after_move:" + FilePrefix + "%(filepath)j",
                        "--print", "before_dl:" + DurationPrefix + "%(duration)j" });
                    args.AddRange(FormatArguments(options));
                    args.AddRange(Aria2Arguments());
                    args.Add("--"); args.Add(entry.Url);
                    Report(progress, entry, MediaDownloadState.Resolving, 0, "获取下载地址", completed, selected.Count);
                    double? duration = entry.DurationSeconds;
                    Action<string> onLine = line => {
                        if (line.StartsWith(FilePrefix, StringComparison.Ordinal))
                        {
                            try { path = JsonConvert.DeserializeObject<string>(line.Substring(FilePrefix.Length)); } catch (JsonException) { }
                        }
                        else if (line.StartsWith(DurationPrefix, StringComparison.Ordinal))
                        {
                            try { duration = PositiveNumber(JToken.Parse(line.Substring(DurationPrefix.Length))) ?? duration; } catch (JsonException) { }
                        }
                        else if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                        {
                            try
                            {
                                JObject p = JObject.Parse(line.Substring(ProgressPrefix.Length));
                                long? received = (long?)p["downloaded_bytes"];
                                long? total = (long?)p["total_bytes"] ?? (long?)p["total_bytes_estimate"];
                                double percent = total > 0 ? Math.Min(100, 100.0 * (received ?? 0) / total.Value) : 0;
                                Report(progress, entry, MediaDownloadState.Downloading, percent, "下载中", completed, selected.Count,
                                    received, total, PositiveNumber(p["speed"]));
                            }
                            catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is OverflowException) { }
                        }
                        else if (line.StartsWith("[Merger]", StringComparison.Ordinal) || line.StartsWith("[ExtractAudio]", StringComparison.Ordinal)
                            || line.StartsWith("[VideoRemuxer]", StringComparison.Ordinal))
                            Report(progress, entry, MediaDownloadState.Processing, 100, "合并或转换媒体", completed, selected.Count);
                    };
                    var output = await MediaProcess.RunAsync(YtDlp, args, directory, onLine, false, token).ConfigureAwait(false);
                    if (output.ExitCode != 0 && (output.StandardError ?? "").Contains("HTTP Error 416"))
                    {
                        // A partial file left by an earlier attempt no longer matches the server's copy.
                        // Restart this item once without resuming; the option must come before the "--" separator.
                        args.Insert(args.Count - 2, "--no-continue");
                        output = await MediaProcess.RunAsync(YtDlp, args, directory, onLine, false, token).ConfigureAwait(false);
                    }
                    if (output.ExitCode != 0) throw new InvalidOperationException(FriendlyError(output.StandardError, options.Session));
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidOperationException("下载器没有产生完整的最终文件，未标记为完成。");
                    path = Path.GetFullPath(path);
                    string outputRoot = Path.GetFullPath(options.OutputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!path.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("下载器返回的文件超出目标目录。");
                    if (options.Mode == MediaDownloadMode.VideoOnly)
                    {
                        Report(progress, entry, MediaDownloadState.Processing, 100, "保留视频轨道", completed, selected.Count);
                        await RemoveAudioAsync(path, token).ConfigureAwait(false);
                    }
                    // Use the duration reported for this particular item, never a playlist's aggregate duration.
                    entry.DurationSeconds = duration;
                    var verificationEntry = new MediaEntry { Id = entry.Id, Title = entry.Title, Url = entry.Url,
                        DurationSeconds = duration, PlaylistIndex = entry.PlaylistIndex };
                    Report(progress, entry, MediaDownloadState.Verifying, 0, "全程解码校验与 SHA-256 检查", completed, selected.Count);
                    MediaDownloadResult result = await VerifyCoreAsync(path, verificationEntry, options, token, value =>
                        Report(progress, entry, MediaDownloadState.Verifying, value, "全程解码校验", completed, selected.Count), false).ConfigureAwait(false);
                    result.Entry = entry;
                    if (result.Cancelled) throw new OperationCanceledException(token);
                    if (!result.Success)
                    {
                        // A completed-but-damaged final file would otherwise be skipped forever by
                        // --no-overwrites. Preserve it for inspection, and let retry obtain fresh bytes.
                        string quarantined = path + ".failed-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                        File.Move(path, quarantined);
                        path = quarantined;
                        throw new InvalidOperationException(result.Error + " 已保留损坏文件；重试将重新下载。");
                    }
                    results.Add(result);
                    completed++;
                    Report(progress, entry, MediaDownloadState.Completed, 100, "已完成并通过完整性校验", completed, selected.Count);
                }
                catch (OperationCanceledException)
                {
                    results.Add(new MediaDownloadResult { Entry = entry, FilePath = path, Cancelled = true, Error = "已取消，保留临时文件供下次续传。" });
                    Report(progress, entry, MediaDownloadState.Cancelled, 0, "已取消", completed, selected.Count);
                }
                catch (Exception ex)
                {
                    string error = FriendlyError(ex.Message, options.Session);
                    results.Add(new MediaDownloadResult { Entry = entry, FilePath = path, Error = error });
                    Report(progress, entry, MediaDownloadState.Failed, 0, error, completed, selected.Count);
                }
            }
            return results;
        }

        public Task<MediaDownloadResult> VerifyAsync(string file, MediaEntry entry, MediaDownloadOptions options, CancellationToken token)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (options == null) throw new ArgumentNullException(nameof(options));
            return VerifyCoreAsync(Path.GetFullPath(file), entry, options, token, null, true);
        }

        private async Task<MediaDownloadResult> VerifyCoreAsync(string file, MediaEntry entry, MediaDownloadOptions options,
            CancellationToken token, Action<double> progress, bool comparePrevious)
        {
            var result = new MediaDownloadResult { Entry = entry, FilePath = file };
            string receipt = VerificationReceiptPath(file);
            string legacyReceipt = file + ".verification.json";
            FileStream verificationLock = null;
            try
            {
                EnsureExecutable(FFmpeg);
                token.ThrowIfCancellationRequested();
                string previousHash = null;
                long? previousSize = null;
                string previousReceipt = File.Exists(receipt) ? receipt : legacyReceipt;
                if (comparePrevious && File.Exists(previousReceipt))
                {
                    JObject previous;
                    try { previous = JObject.Parse(File.ReadAllText(previousReceipt)); }
                    catch (JsonException) { throw new InvalidOperationException("已有校验记录无法读取，请保留该记录并重新下载。"); }
                    previousHash = (string)previous["Sha256"];
                    previousSize = (long?)previous["FileSize"];
                    if (previousHash == null || !Regex.IsMatch(previousHash, "^[0-9a-fA-F]{64}$") || previousSize == null)
                        throw new InvalidOperationException("已有校验记录不完整，请保留该记录并重新下载。");
                }
                // Public rechecks preserve their historical baseline even on failure/cancel.
                // Otherwise a second recheck could silently bless a modified, decodable file.
                // Fresh download verification may discard a stale receipt and issue a new one.
                if (!comparePrevious && File.Exists(receipt)) File.Delete(receipt);
                if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new InvalidOperationException("文件不存在或大小为零。");
                // Keep the exact bytes stable across probe, decode, hashing and receipt creation.
                // FFmpeg can still open another read handle; other writers/replacements cannot.
                verificationLock = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("临时文件不能标记为完整。");
                ProbeResult probe = await ProbeAsync(file, token).ConfigureAwait(false);
                bool audioOnly = options.Mode == MediaDownloadMode.AudioM4a || options.Mode == MediaDownloadMode.AudioMp3;
                if (audioOnly && (!probe.Audio || probe.Video)) throw new InvalidOperationException("音频文件的轨道不符合所选下载模式。");
                if (options.Mode == MediaDownloadMode.VideoOnly && (!probe.Video || probe.Audio)) throw new InvalidOperationException("仅视频文件的轨道不符合所选下载模式。");
                if (options.Mode == MediaDownloadMode.VideoAndAudio && (!probe.Video || !probe.Audio)) throw new InvalidOperationException("文件缺少视频或音频轨道。");
                if (probe.Duration <= 0 || double.IsNaN(probe.Duration) || double.IsInfinity(probe.Duration)) throw new InvalidOperationException("无法确定媒体时长，未标记为完整。");
                if (options.MaximumHeight > 0 && probe.Height > options.MaximumHeight)
                    throw new InvalidOperationException("文件分辨率超过所选上限。");
                if (entry.DurationSeconds > 0 && Math.Abs(probe.Duration - entry.DurationSeconds.Value) > Math.Max(2.0, entry.DurationSeconds.Value * 0.03))
                    throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                        "时长不符：预期 {0:0.0} 秒，实际 {1:0.0} 秒。", entry.DurationSeconds.Value, probe.Duration));
                var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-xerror", "-err_detect", "explode", "-i", file };
                if (!audioOnly) args.AddRange(new[] { "-map", "0:v:0" });
                if (options.Mode != MediaDownloadMode.VideoOnly) args.AddRange(new[] { "-map", "0:a:0" });
                args.AddRange(new[] { "-progress", "pipe:1", "-nostats", "-f", "null", "NUL" });
                double decodedSeconds = 0;
                var decoded = await MediaProcess.RunAsync(FFmpeg, args, directory, line => {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
                    {
                        double micros;
                        if (double.TryParse(line.Substring(line.IndexOf('=') + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out micros))
                        {
                            decodedSeconds = Math.Max(decodedSeconds, micros / 1000000.0);
                            progress?.Invoke(Math.Min(99, decodedSeconds * 100 / probe.Duration));
                        }
                    }
                }, false, token).ConfigureAwait(false);
                if (decoded.ExitCode != 0 || HasDecodeErrors(decoded.StandardError))
                    throw new InvalidOperationException("全程解码发现损坏或缺失数据，文件未通过完整性校验。");
                if (decodedSeconds < probe.Duration - Math.Max(2.0, probe.Duration * 0.03))
                    throw new InvalidOperationException("解码未到达媒体结尾，文件未通过完整性校验。");
                string hash = await HashFileAsync(file, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (previousHash != null && (!string.Equals(previousHash, hash, StringComparison.OrdinalIgnoreCase) || previousSize != new FileInfo(file).Length))
                    throw new InvalidOperationException("文件与先前的 SHA-256 或大小记录不符，内容已发生变化。请重新下载。");
                var proof = new {
                    SchemaVersion = 1, VerifiedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    FileName = Path.GetFileName(file), FileSize = new FileInfo(file).Length, Sha256 = hash,
                    SourceUrl = PublicUrl(entry.Url), MediaId = entry.Id, Title = entry.Title,
                    ExpectedDurationSeconds = entry.DurationSeconds, ActualDurationSeconds = probe.Duration,
                    DecodedSeconds = decodedSeconds, Mode = options.Mode.ToString(),
                    HasVideo = probe.Video, HasAudio = probe.Audio, Height = probe.Height,
                    Verification = "FFmpeg full decode with -xerror -err_detect explode; SHA-256"
                };
                string temporary = receipt + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(receipt));
                    File.WriteAllText(temporary, JsonConvert.SerializeObject(proof, Formatting.Indented), new UTF8Encoding(false));
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(receipt)) File.Replace(temporary, receipt, null); else File.Move(temporary, receipt);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                // Retain the baseline in app data; successful downloads/rechecks leave no sidecar.
                // Failed/cancelled checks preserve legacy records until they can be validated.
                if (File.Exists(legacyReceipt)) File.Delete(legacyReceipt);
                result.Success = true; result.ReceiptPath = receipt; result.Sha256 = hash; result.ActualDurationSeconds = probe.Duration;
            }
            catch (OperationCanceledException) { result.Cancelled = true; result.Error = "完整性校验已取消。"; }
            catch (Exception ex) { result.Error = FriendlyError(ex.Message, options.Session); }
            finally { verificationLock?.Dispose(); }
            return result;
        }

        private static string VerificationReceiptPath(string file)
        {
            string normalized = Path.GetFullPath(file).Replace('/', '\\').ToUpperInvariant();
            using (SHA256 sha = SHA256.Create())
            {
                string key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", "").ToLowerInvariant();
                return Path.Combine(Services.AppPaths.DataDirectory, "Storage", "Verification", key + ".json");
            }
        }

        private async Task<ProbeResult> ProbeAsync(string file, CancellationToken token)
        {
            string ffprobe = Path.Combine(directory, "ffprobe.exe");
            if (File.Exists(ffprobe))
            {
                ProcessOutput p = await MediaProcess.RunAsync(ffprobe, new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", file }, directory, null, true, token).ConfigureAwait(false);
                if (p.ExitCode != 0) throw new InvalidOperationException("无法读取媒体文件信息。");
                JObject json = JObject.Parse(p.StandardOutput);
                var streams = (json["streams"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                return new ProbeResult {
                    Audio = streams.Any(x => (string)x["codec_type"] == "audio"),
                    Video = streams.Any(x => (string)x["codec_type"] == "video" && (int?)x["disposition"]?["attached_pic"] != 1),
                    Height = streams.Where(x => (string)x["codec_type"] == "video").Select(x => (int?)x["height"] ?? 0).DefaultIfEmpty().Max(),
                    Duration = PositiveNumber(json["format"]?["duration"]) ?? streams.Select(x => PositiveNumber(x["duration"]) ?? 0).DefaultIfEmpty().Max()
                };
            }
            ProcessOutput fallback = await MediaProcess.RunAsync(FFmpeg, new[] { "-hide_banner", "-nostdin", "-i", file }, directory, null, false, token).ConfigureAwait(false);
            Match duration = Regex.Match(fallback.StandardError, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            Match resolution = Regex.Match(fallback.StandardError, @"Video:[^\r\n]*?\b\d{2,5}x(\d{2,5})\b");
            return new ProbeResult {
                Audio = Regex.IsMatch(fallback.StandardError, @"Stream #[^\r\n]+: Audio:"),
                Video = Regex.IsMatch(fallback.StandardError, @"Stream #[^\r\n]+: Video:"),
                Height = resolution.Success ? int.Parse(resolution.Groups[1].Value, CultureInfo.InvariantCulture) : 0,
                Duration = duration.Success ? double.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                    + double.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                    + double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture) : 0
            };
        }

        private async Task RemoveAudioAsync(string file, CancellationToken token)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(file), ".video-" + Guid.NewGuid().ToString("N") + Path.GetExtension(file));
            try
            {
                ProcessOutput p = await MediaProcess.RunAsync(FFmpeg, new[] { "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", file,
                    "-map", "0:v:0", "-c:v", "copy", "-an", temporary }, directory, null, false, token).ConfigureAwait(false);
                if (p.ExitCode != 0 || !File.Exists(temporary)) throw new InvalidOperationException("分离视频轨道失败。");
                File.Replace(temporary, file, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>
        /// Optional multi-connection transfer for single-file streams. Off by default: used only when
        /// BDOWNLOADER_ARIA2=1 is set and aria2c.exe sits next to yt-dlp.exe. aria2 writes its log to
        /// %LOCALAPPDATA%\BDownloader\aria2.log so a failure can be diagnosed.
        /// </summary>
        private IEnumerable<string> Aria2Arguments()
        {
            if (Environment.GetEnvironmentVariable("BDOWNLOADER_ARIA2") != "1") return Enumerable.Empty<string>();
            string aria2 = Path.Combine(directory, "aria2c.exe");
            if (!File.Exists(aria2)) return Enumerable.Empty<string>();
            string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BDownloader");
            Directory.CreateDirectory(logDirectory);
            string log = Path.Combine(logDirectory, "aria2.log");
            return new[] { "--external-downloader", aria2,
                "--external-downloader-args", "aria2c:-x8 -s8 -k1M --file-allocation=none --auto-file-renaming=false --log=\"" + log + "\" --log-level=notice" };
        }

        private List<string> BaseArguments(MediaSession session)
        {
            var args = new List<string> { "--ignore-config", "--encoding", "utf-8", "--socket-timeout", "30", "--extractor-retries", "3", "--no-warnings" };
            // Use the bundled lightweight runtime, independently of installed runtimes.
            args.Add("--no-js-runtimes");
            string quickJs = Path.Combine(directory, "qjs.exe");
            if (File.Exists(quickJs)) args.AddRange(new[] { "--js-runtimes", "quickjs:" + quickJs });
            if (session != null && !string.IsNullOrWhiteSpace(session.CookiesFile))
            {
                if (!File.Exists(session.CookiesFile)) throw new FileNotFoundException("登录 Cookie 文件不存在，请重新登录或导入。");
                args.AddRange(new[] { "--cookies", Path.GetFullPath(session.CookiesFile) });
            }
            else if (session != null && !string.IsNullOrWhiteSpace(session.BrowserCookies))
            {
                string browser = session.BrowserCookies.ToLowerInvariant();
                if (!new[] { "chrome", "edge", "firefox", "brave", "opera", "vivaldi", "chromium" }.Contains(browser))
                    throw new ArgumentException("不支持的浏览器 Cookie 来源。");
                args.AddRange(new[] { "--cookies-from-browser", browser });
            }
            return args;
        }

        public static IList<string> FormatArguments(MediaDownloadOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            string limit = options.MaximumHeight > 0 ? "[height<=" + options.MaximumHeight.ToString(CultureInfo.InvariantCulture) + "]" : "";
            if (options.Mode == MediaDownloadMode.AudioM4a || options.Mode == MediaDownloadMode.AudioMp3)
                return new[] { "-f", "ba/b", "--extract-audio", "--audio-format", options.Mode == MediaDownloadMode.AudioMp3 ? "mp3" : "m4a", "--audio-quality", "0" };
            return new[] { "-f", options.Mode == MediaDownloadMode.VideoOnly ? "bv" + limit + "/b" + limit : "bv*" + limit + "+ba/b" + limit,
                "--merge-output-format", "mp4", "--remux-video", "mp4" };
        }

        private static string OutputTemplate(MediaEntry entry, MediaDownloadOptions options)
        {
            string prefix = entry.PlaylistIndex > 0 ? entry.PlaylistIndex.ToString("D3", CultureInfo.InvariantCulture) + " " : "";
            string quality = options.MaximumHeight > 0 ? options.MaximumHeight.ToString(CultureInfo.InvariantCulture) + "p" : "best";
            return prefix + "%(title).100B [%(id)s] " + options.Mode + "_" + quality + ".%(ext)s";
        }

        private static async Task<string> HashFileAsync(string file, CancellationToken token)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            using (SHA256 sha = SHA256.Create())
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                    sha.TransformBlock(buffer, 0, read, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void ValidateOptions(MediaDownloadOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new ArgumentException("请选择保存目录。");
            if (options.MaximumHeight < 0 || options.MaximumHeight > 16384) throw new ArgumentException("清晰度上限无效。");
            if (!Enum.IsDefined(typeof(MediaDownloadMode), options.Mode)) throw new ArgumentException("下载模式无效。");
            Directory.CreateDirectory(Path.GetFullPath(options.OutputDirectory));
        }

        private static void ValidateUrl(string url)
        {
            if (!IsSupportedUrl(url)) throw new ArgumentException("请输入哔哩哔哩或 YouTube 的 HTTP/HTTPS 链接。");
        }

        public static bool IsSupportedUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            string host = uri.DnsSafeHost.ToLowerInvariant();
            return new[] { "bilibili.com", "b23.tv", "youtube.com", "youtu.be" }.Any(x => host == x || host.EndsWith("." + x, StringComparison.Ordinal));
        }

        private static double? PositiveNumber(JToken token)
        {
            double value;
            return token != null && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && value > 0 && !double.IsInfinity(value) && !double.IsNaN(value) ? (double?)value : null;
        }

        private static string PublicUrl(string url)
        {
            if (!IsSupportedUrl(url)) return null;
            var uri = new UriBuilder(url) { Fragment = "" };
            var allowed = new HashSet<string>(new[] { "v", "p", "list", "sid", "bvid" }, StringComparer.OrdinalIgnoreCase);
            uri.Query = string.Join("&", uri.Query.TrimStart('?').Split('&').Where(x => allowed.Contains(x.Split('=')[0])));
            return uri.Uri.AbsoluteUri;
        }

        private static string FriendlyError(string error, MediaSession session)
        {
            if (string.IsNullOrWhiteSpace(error)) return "操作失败，请检查网络、账号权限及下载组件后重试。";
            string message = error;
            if (!string.IsNullOrEmpty(session?.CookiesFile)) message = message.Replace(session.CookiesFile, "[Cookie 文件]");
            message = Regex.Replace(message, @"(?i)(SESSDATA|bili_jct|authorization|cookie|token|signature|sig|key)\s*[:=]\s*[^\s;,&]+", "$1=[已隐藏]");
            message = Regex.Replace(message, @"https?://[^\s]+", match => {
                Uri uri; return Uri.TryCreate(match.Value, UriKind.Absolute, out uri) ? uri.GetLeftPart(UriPartial.Path) : "[链接]";
            });
            string[] lines = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            message = string.Join(" ", lines.Where(x => !x.StartsWith("[debug]", StringComparison.OrdinalIgnoreCase)
                && !x.StartsWith(ProgressPrefix, StringComparison.Ordinal)).Take(5));
            return message.Length <= 800 ? message : message.Substring(0, 800);
        }

        private static void EnsureExecutable(string file)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("缺少组件 " + Path.GetFileName(file) + "，请重新安装完整软件包。");
        }

        private static bool HasDecodeErrors(string stderr)
        {
            // Bundled libdav1d writes this informational size-cap notice directly to stderr,
            // bypassing FFmpeg's -v error filter. It is unrelated to the bitstream's integrity.
            // Keep every other stderr line fatal, including decoder errors with exit code 0.
            bool previousBenignNotice = false;
            foreach (string raw in (stderr ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (Regex.IsMatch(line, @"^\[libdav1d @ [0-9a-fA-F]+\] (?:Frame size limit reduced from \d+ to \d+\.|Unknown Metadata OBU type 6)$"))
                {
                    // Older bundled dav1d also reports unsupported optional metadata OBU 6;
                    // it still decodes every audio/video frame. Only this metadata type is allowed.
                    previousBenignNotice = true;
                    continue;
                }
                if (previousBenignNotice && Regex.IsMatch(line, @"^Last message repeated \d+ times$")) continue;
                return true;
            }
            return false;
        }

        private static void Report(IProgress<MediaProgress> progress, MediaEntry entry, MediaDownloadState state, double percent,
            string message, int complete, int total, long? received = null, long? bytes = null, double? speed = null)
        {
            progress?.Report(new MediaProgress { Entry = entry, State = state, Percent = percent, Message = message,
                CompletedCount = complete, TotalCount = total, DownloadedBytes = received, TotalBytes = bytes, BytesPerSecond = speed });
        }

        private sealed class ProbeResult { public bool Audio; public bool Video; public double Duration; public int Height; }
    }

    internal sealed class ProcessOutput
    {
        public int ExitCode;
        public string StandardOutput;
        public string StandardError;
    }

    /// <summary>Both pipes are drained concurrently. Cancellation terminates the whole process tree.</summary>
    internal static class MediaProcess
    {
        public static async Task<ProcessOutput> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
            Action<string> onLine, bool captureOutput, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo {
                FileName = executable, Arguments = string.Join(" ", arguments.Select(QuoteArgument)), WorkingDirectory = workingDirectory,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            using (var process = new Process { StartInfo = start, EnableRaisingEvents = true })
            using (var job = new KillOnCloseJob())
            {
                var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.Exited += (sender, e) => exited.TrySetResult(true);
                if (!process.Start()) throw new InvalidOperationException("无法启动媒体组件。");
                job.Assign(process);
                process.StandardInput.Close();
                using (token.Register(() => { job.Kill(); KillProcessTree(process); }))
                {
                    Task<string> stdout = DrainAsync(process.StandardOutput, onLine, captureOutput, 40 * 1024 * 1024, false);
                    Task<string> stderr = DrainAsync(process.StandardError, onLine, true, 64 * 1024, true);
                    if (process.HasExited) exited.TrySetResult(true);
                    await exited.Task.ConfigureAwait(false);
                    string[] outputs = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return new ProcessOutput { ExitCode = process.ExitCode, StandardOutput = outputs[0], StandardError = outputs[1] };
                }
            }
        }

        private static async Task<string> DrainAsync(StreamReader stream, Action<string> onLine, bool capture, int limit, bool keepTail)
        {
            var builder = new StringBuilder();
            Exception readError = null;
            string line;
            while ((line = await stream.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                // Keep draining even if a progress subscriber or malformed progress record throws.
                // Otherwise the child may fill a pipe and never exit.
                try { onLine?.Invoke(line); }
                catch (Exception ex) { if (readError == null) readError = ex; }
                if (!capture) continue;
                if (keepTail)
                {
                    builder.AppendLine(line);
                    if (builder.Length > limit) builder.Remove(0, builder.Length - limit);
                }
                else if (builder.Length + line.Length + 2 < limit) builder.AppendLine(line);
                else if (readError == null) readError = new InvalidOperationException("解析结果过大，请拆分播放列表后重试。");
            }
            if (readError != null) throw readError;
            return builder.ToString();
        }

        /// <summary>Windows CommandLineToArgvW-compatible escaping, including trailing backslashes.</summary>
        public static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";
            var builder = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { builder.Append('\\', backslashes * 2 + 1); builder.Append('"'); backslashes = 0; continue; }
                builder.Append('\\', backslashes); backslashes = 0; builder.Append(c);
            }
            builder.Append('\\', backslashes * 2); builder.Append('"');
            return builder.ToString();
        }

        private static void KillProcessTree(Process process)
        {
            try
            {
                if (process.HasExited) return;
                using (var kill = Process.Start(new ProcessStartInfo {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"),
                    Arguments = "/PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " /T /F", UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                }))
                { if (kill != null) { kill.StandardOutput.ReadToEnd(); kill.StandardError.ReadToEnd(); kill.WaitForExit(5000); } }
                if (!process.HasExited) process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { try { process.Kill(); } catch (InvalidOperationException) { } }
        }
    }

    internal sealed class KillOnCloseJob : IDisposable
    {
        private IntPtr handle;
        public KillOnCloseJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) return;
            var info = new ExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf(info);
            IntPtr memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, memory, false);
                if (!SetInformationJobObject(handle, 9, memory, (uint)size)) Kill();
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        public void Assign(Process process) { if (handle != IntPtr.Zero) AssignProcessToJobObject(handle, process.Handle); }
        public void Kill() { IntPtr current = Interlocked.Exchange(ref handle, IntPtr.Zero); if (current != IntPtr.Zero) CloseHandle(current); }
        public void Dispose() { Kill(); }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
        { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters
        { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
        { public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
