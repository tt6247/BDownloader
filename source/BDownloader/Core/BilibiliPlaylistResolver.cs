using BDownloader.Services;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BDownloader.Core
{
    public sealed class PlaylistEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public double DurationSeconds { get; set; }
    }

    public sealed class PlaylistResult
    {
        public string Title { get; set; }
        public int ExpectedCount { get; set; }
        public bool IsComplete { get; set; }
        public List<PlaylistEntry> Entries { get; set; } = new List<PlaylistEntry>();
    }

    /// <summary>
    /// Expands Bilibili series and collections before asking a media extractor to
    /// resolve individual videos. The bvid/oid in a player URL select the current
    /// video and must never be mistaken for the entire playlist.
    /// </summary>
    public sealed class BilibiliPlaylistResolver
    {
        private const int PageSize = 30;
        private const int MaximumEntries = 100000;
        private readonly Func<string, CancellationToken, Task<string>> fetch;

        public BilibiliPlaylistResolver(Func<string, CancellationToken, Task<string>> fetch = null)
        {
            this.fetch = fetch ?? FetchAuthenticatedAsync;
        }

        public static bool IsListUrl(string url)
        {
            ListAddress ignored;
            return TryParse(url, out ignored);
        }

        /// <summary>Recognises a single Bilibili video page (/video/BV...) and returns its BV id.</summary>
        public static bool TryGetVideoId(string url, out string bvid)
        {
            bvid = null;
            Uri uri;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (uri.Host != "www.bilibili.com" && uri.Host != "bilibili.com") return false;
            Match match = Regex.Match(uri.AbsolutePath, @"^/video/(BV[0-9A-Za-z]{10})/?$");
            if (!match.Success) return false;
            bvid = match.Groups[1].Value;
            return true;
        }

        /// <summary>Lists every part (分P) of a video from the view API, which reports the authoritative total.</summary>
        public async Task<PlaylistResult> ResolveVideoPagesAsync(string bvid, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            JObject data = await GetDataAsync("https://api.bilibili.com/x/web-interface/view?bvid=" + bvid, token).ConfigureAwait(false);
            return ParseVideoPages(data, bvid);
        }

        /// <summary>Parses a view API payload. Public for deterministic offline regression tests.</summary>
        public static PlaylistResult ParseVideoPages(JObject data, string bvid)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var pages = data["pages"] as JArray;
            if (pages == null || pages.Count == 0) throw Incomplete("视频分P数据为空");
            int? declared = ReadCount(data["videos"]);
            if (declared.HasValue && declared.Value != pages.Count) throw Incomplete("分P总数与分P列表不一致");
            var result = new PlaylistResult { Title = (string)data["title"] ?? bvid, ExpectedCount = pages.Count, IsComplete = true };
            var numbers = new HashSet<int>();
            foreach (JToken page in pages)
            {
                int? number = ReadCount(page["page"]);
                if (!number.HasValue || number.Value <= 0 || !numbers.Add(number.Value)) throw Incomplete("分P序号无效或重复");
                string part = (string)page["part"];
                result.Entries.Add(new PlaylistEntry
                {
                    Id = bvid + "_p" + number.Value.ToString(CultureInfo.InvariantCulture),
                    Title = "P" + number.Value.ToString(CultureInfo.InvariantCulture) + " " + (string.IsNullOrWhiteSpace(part) ? bvid : part),
                    Url = "https://www.bilibili.com/video/" + bvid + "?p=" + number.Value.ToString(CultureInfo.InvariantCulture),
                    DurationSeconds = ReadDuration(page["duration"])
                });
            }
            return result;
        }

        public async Task<PlaylistResult> ResolveAsync(string url, CancellationToken token)
        {
            ListAddress address;
            if (!TryParse(url, out address))
                throw new ArgumentException("不支持的哔哩哔哩列表链接；请提供 list/?sid= 或空间 lists/ 链接。", nameof(url));

            token.ThrowIfCancellationRequested();
            JObject metadata = null;
            if (!address.IsSeason)
            {
                try
                {
                    metadata = await GetDataAsync("https://api.bilibili.com/x/series/series?series_id=" + address.ListId, token).ConfigureAwait(false);
                    var owner = ReadInteger(metadata.SelectToken("meta.mid"));
                    if (owner.HasValue && owner.Value != address.OwnerId)
                        throw new PlaylistApiException(-404, "列表所属用户与链接不符");
                    var seriesId = ReadInteger(metadata.SelectToken("meta.series_id"));
                    if (seriesId.HasValue && seriesId.Value != address.ListId)
                        throw Incomplete("服务器返回了错误的列表编号");
                }
                catch (PlaylistApiException error) when (!address.ExplicitType && (error.Code == -404 || error.Code == -400))
                {
                    // IDs belong to separate namespaces. Only a definite missing
                    // series may be retried as a season; never hide a login/risk error.
                    address.IsSeason = true;
                    metadata = null;
                }
            }

            string title = (string)metadata?.SelectToken("meta.name");
            int? metadataCount = ReadCount(metadata?.SelectToken("meta.total"));
            var result = new PlaylistResult { Title = title ?? "哔哩哔哩列表 " + address.ListId };
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int? expected = null;
            int? returnedPageSize = null;

            for (int page = 1; page <= MaximumEntries; page++)
            {
                token.ThrowIfCancellationRequested();
                string api = address.IsSeason
                    ? "https://api.bilibili.com/x/polymer/web-space/seasons_archives_list?mid=" + address.OwnerId + "&season_id=" + address.ListId + "&page_num=" + page + "&page_size=" + PageSize + "&sort_reverse=false"
                    : "https://api.bilibili.com/x/series/archives?mid=" + address.OwnerId + "&series_id=" + address.ListId + "&only_normal=false&sort=asc&pn=" + page + "&ps=" + PageSize;
                JObject data = await GetDataAsync(api, token).ConfigureAwait(false);
                var owner = ReadInteger(data.SelectToken("meta.mid"));
                if (owner.HasValue && owner.Value != address.OwnerId)
                    throw Incomplete("服务器返回了另一位用户的列表");
                var listId = ReadInteger(data.SelectToken(address.IsSeason ? "meta.season_id" : "meta.series_id"));
                if (listId.HasValue && listId.Value != address.ListId)
                    throw Incomplete("服务器返回了错误的列表编号");
                int? pageCount = ReadCount(data.SelectToken("page.total"));
                if (!pageCount.HasValue)
                    throw Incomplete("服务器未提供列表总数，无法确认是否获取了全部视频");
                if (expected.HasValue && expected.Value != pageCount.Value)
                    throw Incomplete("读取期间列表总数发生变化，请重新解析");
                expected = pageCount.Value;
                if (expected.Value > MaximumEntries)
                    throw Incomplete("列表超过支持的最大条目数 " + MaximumEntries);
                if (metadataCount.HasValue && metadataCount.Value != expected.Value)
                    throw Incomplete("列表元数据和分页总数不一致，可能有失效或受限视频");
                int? currentMetaCount = ReadCount(data.SelectToken("meta.total"));
                if (currentMetaCount.HasValue && currentMetaCount.Value != expected.Value)
                    throw Incomplete("合集元数据和分页总数不一致，可能有失效或受限视频");

                int? reportedPage = ReadCount(data.SelectToken(address.IsSeason ? "page.page_num" : "page.num"));
                if (reportedPage.HasValue && reportedPage.Value != page)
                    throw Incomplete("服务器返回了错误的分页序号");
                int? size = ReadCount(data.SelectToken(address.IsSeason ? "page.page_size" : "page.size"));
                if (size.HasValue)
                {
                    if (size.Value == 0 || (returnedPageSize.HasValue && returnedPageSize.Value != size.Value))
                        throw Incomplete("服务器分页大小无效或发生变化");
                    returnedPageSize = size;
                }
                var archives = data["archives"] as JArray;
                if (archives == null)
                    throw Incomplete("服务器没有返回视频列表");
                if (returnedPageSize.HasValue && archives.Count > returnedPageSize.Value)
                    throw Incomplete("服务器分页条目数超过声明的分页大小");
                if (page == 1 && address.IsSeason && !string.IsNullOrWhiteSpace((string)data.SelectToken("meta.name")))
                    result.Title = (string)data.SelectToken("meta.name");

                foreach (JToken archive in archives)
                {
                    string bvid = (string)archive["bvid"];
                    long? aid = ReadInteger(archive["aid"]);
                    string id = !string.IsNullOrWhiteSpace(bvid) && Regex.IsMatch(bvid, @"^BV[0-9A-Za-z]{10}$")
                        ? bvid : aid.HasValue && aid.Value > 0 ? "av" + aid.Value.ToString(CultureInfo.InvariantCulture) : null;
                    if (id == null)
                        throw Incomplete("列表中包含失效或无法识别的视频，已停止以免遗漏");
                    if (!ids.Add(id))
                        throw Incomplete("分页中出现重复视频，无法确认列表完整性");
                    if (result.Entries.Count >= expected.Value)
                        throw Incomplete("服务器返回的视频数超过列表总数");
                    result.Entries.Add(new PlaylistEntry
                    {
                        Id = id,
                        Title = (string)archive["title"] ?? id,
                        Url = "https://www.bilibili.com/video/" + id,
                        DurationSeconds = ReadDuration(archive["duration"])
                    });
                }

                if (result.Entries.Count == expected.Value)
                {
                    result.ExpectedCount = expected.Value;
                    result.IsComplete = true;
                    return result;
                }
                if (archives.Count == 0 || (returnedPageSize.HasValue && archives.Count < returnedPageSize.Value))
                    throw Incomplete("已获得 " + result.Entries.Count + " / " + expected.Value + " 个视频，服务器提前结束分页");
            }
            throw Incomplete("分页次数超出上限");
        }

        private async Task<JObject> GetDataAsync(string url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string json = await fetch(url, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception error) when (error is JsonException || error is ArgumentException)
            { throw new InvalidOperationException("哔哩哔哩列表接口返回非 JSON 内容，可能被风控拦截；请登录后重试。", error); }
            long? code = ReadInteger(root["code"]);
            if (!code.HasValue)
                throw new InvalidOperationException("哔哩哔哩列表接口响应格式无效；未将当前视频当作整个列表。 ");
            if (code.Value != 0)
                throw new PlaylistApiException(code.Value, (string)root["message"] ?? "未知错误");
            var data = root["data"] as JObject;
            if (data == null)
                throw Incomplete("列表数据为空，列表可能不存在或无访问权限");
            return data;
        }

        private static InvalidOperationException Incomplete(string detail)
        {
            return new InvalidOperationException("哔哩哔哩列表解析不完整：" + detail + "。未创建不完整的批量任务。");
        }

        private static long? ReadInteger(JToken value)
        {
            long number;
            return value != null && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? (long?)number : null;
        }

        private static int? ReadCount(JToken value)
        {
            long? number = ReadInteger(value);
            return number.HasValue && number.Value >= 0 && number.Value <= int.MaxValue ? (int?)number.Value : null;
        }

        private static double ReadDuration(JToken value)
        {
            double duration;
            return value != null && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration)
                && !double.IsInfinity(duration) && !double.IsNaN(duration) && duration > 0 ? duration : 0;
        }

        private static bool TryParse(string url, out ListAddress address)
        {
            address = null;
            Uri uri;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim().Replace("&amp;", "&"), UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (uri.Host != "www.bilibili.com" && uri.Host != "bilibili.com" && uri.Host != "space.bilibili.com") return false;
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string pair in uri.Query.TrimStart('?').Split('&'))
            {
                int separator = pair.IndexOf('=');
                if (separator < 1) continue;
                string key = Uri.UnescapeDataString(pair.Substring(0, separator));
                string value = Uri.UnescapeDataString(pair.Substring(separator + 1));
                if (query.ContainsKey(key) && query[key] != value) return false;
                query[key] = value;
            }
            string listId;
            Match match;
            bool season;
            bool pathType = false;
            if (uri.Host == "space.bilibili.com")
            {
                match = Regex.Match(uri.AbsolutePath, @"^/(\d+)/(?:lists/(\d+)|channel/(collectiondetail|seriesdetail))/?$");
                if (!match.Success) return false;
                listId = match.Groups[2].Value;
                if (listId.Length == 0 && !query.TryGetValue("sid", out listId)) return false;
                season = match.Groups[3].Value != "seriesdetail";
                pathType = match.Groups[3].Length > 0;
            }
            else
            {
                match = Regex.Match(uri.AbsolutePath, @"^/(?:list|medialist/play)/(\d+)/?$");
                if (!match.Success) return false;
                if (!query.TryGetValue("sid", out listId) && !query.TryGetValue("business_id", out listId)) return false;
                // The modern /list/?sid= player defaults to a space series.
                season = false;
            }
            string type;
            bool queryType = query.TryGetValue("type", out type);
            bool explicitType = queryType || pathType;
            if (queryType)
            {
                if (string.Equals(type, "season", StringComparison.OrdinalIgnoreCase)) season = true;
                else if (string.Equals(type, "series", StringComparison.OrdinalIgnoreCase)) season = false;
                else return false;
            }
            string business;
            if (query.TryGetValue("business", out business))
            {
                if (business == "space_season") { season = true; explicitType = true; }
                else if (business == "space_series") { season = false; explicitType = true; }
                else return false;
            }
            long owner, list;
            if (!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out owner) || owner <= 0
                || !long.TryParse(listId, NumberStyles.None, CultureInfo.InvariantCulture, out list) || list <= 0) return false;
            address = new ListAddress { OwnerId = owner, ListId = list, IsSeason = season, ExplicitType = explicitType };
            return true;
        }

        private static async Task<string> FetchAuthenticatedAsync(string url, CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36";
            request.Referer = "https://www.bilibili.com/";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.CookieContainer = BilibiliLogin.GetCookies() ?? new CookieContainer();
            using (token.Register(request.Abort))
            {
                try
                {
                    using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream))
                    {
                        string result = await reader.ReadToEndAsync().ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        return result;
                    }
                }
                catch (WebException error)
                {
                    token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("无法读取哔哩哔哩列表；请检查网络、代理或登录状态。" + error.Message, error);
                }
            }
        }

        private sealed class ListAddress
        {
            public long OwnerId;
            public long ListId;
            public bool IsSeason;
            public bool ExplicitType;
        }

        private sealed class PlaylistApiException : InvalidOperationException
        {
            public long Code { get; }
            public PlaylistApiException(long code, string message)
                : base("哔哩哔哩列表接口拒绝请求（" + code + "）：" + message + "。请检查登录、访问权限或稍后重试。")
            { Code = code; }
        }
    }
}
