using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BDownloader.Services
{
    public sealed class QrLoginRequest
    {
        public string Url { get; set; }
        public string Key { get; set; }
    }

    public sealed class QrLoginResult
    {
        public int Code { get; set; }
        // The callback URL may contain credentials; do not include it in logs.
        [JsonIgnore] public string Url { get; set; }
        [JsonIgnore] public CookieContainer Cookies { get; set; }
    }

    /// <summary>Standalone Bilibili QR login and per-Windows-user protected sessions.</summary>
    public static class BilibiliLogin
    {
        private static readonly Uri[] CookieOrigins = {
            new Uri("https://passport.bilibili.com/"), new Uri("https://api.bilibili.com/"), new Uri("https://www.bilibili.com/")
        };
        private static readonly string[] CookieNames = { "DedeUserID", "DedeUserID__ckMd5", "SESSDATA", "bili_jct", "sid", "buvid3", "buvid4", "b_nut", "b_lsid" };
        private static string AccountDirectory => Path.Combine(AppPaths.DataDirectory, "Config", "Accounts");
        private static string AccountFile => Path.Combine(AccountDirectory, "bilibili.bin");

        public static bool HasSession => GetCookies() != null;

        public static CookieContainer GetCookies()
        {
            try
            {
                if (!File.Exists(AccountFile) || new FileInfo(AccountFile).Length > 1024 * 1024) return null;
                byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(AccountFile), null, DataProtectionScope.CurrentUser);
                try
                {
                    var saved = JsonConvert.DeserializeObject<SavedSession>(Encoding.UTF8.GetString(clear), new JsonSerializerSettings { MaxDepth = 16, TypeNameHandling = TypeNameHandling.None });
                    if (saved == null || saved.Version != 1 || saved.Cookies == null) return null;
                    var cookies = new CookieContainer();
                    foreach (CookieRecord record in saved.Cookies)
                    {
                        if (record == null || Array.IndexOf(CookieNames, record.Name) < 0 || string.IsNullOrWhiteSpace(record.Value)) continue;
                        try
                        {
                            var cookie = new Cookie(record.Name, record.Value, "/", ".bilibili.com") { Secure = true, HttpOnly = record.HttpOnly };
                            if (record.ExpiresUtc.HasValue) cookie.Expires = record.ExpiresUtc.Value.ToUniversalTime();
                            if (!cookie.Expired) cookies.Add(cookie);
                        }
                        catch (CookieException) { }
                    }
                    return IsAuthenticated(cookies) ? cookies : null;
                }
                finally { Array.Clear(clear, 0, clear.Length); }
            }
            catch (CryptographicException) { return null; }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }

        /// <summary>Returns false for incomplete credentials and preserves the previous session.</summary>
        public static bool SaveCookies(CookieContainer cookies)
        {
            if (!IsAuthenticated(cookies)) return false;
            var normalized = MergeCookies(null, cookies);
            var saved = new SavedSession { Version = 1, Cookies = new List<CookieRecord>() };
            foreach (Cookie cookie in normalized.GetCookies(CookieOrigins[1]))
                saved.Cookies.Add(new CookieRecord { Name = cookie.Name, Value = cookie.Value, HttpOnly = cookie.HttpOnly,
                    ExpiresUtc = cookie.Expires == DateTime.MinValue ? (DateTime?)null : cookie.Expires.ToUniversalTime() });
            byte[] clear = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(saved));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(clear, 0, clear.Length); }
            Directory.CreateDirectory(AccountDirectory);
            string temporary = AccountFile + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(AccountFile)) File.Replace(temporary, AccountFile, null);
                else File.Move(temporary, AccountFile);
                return true;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static void Logout()
        {
            if (File.Exists(AccountFile)) File.Delete(AccountFile);
        }

        public static async Task<QrLoginRequest> GenerateAsync(CancellationToken token)
        {
            var response = await RequestAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", token).ConfigureAwait(false);
            EnsureSuccess(response.Document);
            string url = (string)response.Document.SelectToken("data.url");
            string key = (string)response.Document.SelectToken("data.qrcode_key");
            Uri address;
            if (string.IsNullOrWhiteSpace(key) || !Uri.TryCreate(url, UriKind.Absolute, out address) || address.Scheme != "https"
                || (address.Host != "bilibili.com" && !address.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("登录接口未返回有效的二维码，请稍后重试。");
            return new QrLoginRequest { Url = url, Key = key };
        }

        public static async Task<QrLoginResult> PollAsync(string key, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("二维码密钥不能为空。", nameof(key));
            var response = await RequestAsync("https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + Uri.EscapeDataString(key) + "&source=main_web", token).ConfigureAwait(false);
            EnsureSuccess(response.Document);
            JObject data = response.Document["data"] as JObject;
            int code;
            if (data == null || !int.TryParse((string)data["code"], NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                throw new InvalidOperationException("登录状态响应无效，请刷新二维码。");
            string url = (string)data["url"];
            return new QrLoginResult { Code = code, Url = url, Cookies = code == 0 ? MergeCookies(url, response.Cookies) : new CookieContainer() };
        }

        private static bool IsAuthenticated(CookieContainer cookies)
        {
            if (cookies == null) return false;
            foreach (string name in new[] { "DedeUserID", "SESSDATA", "bili_jct" })
                if (FindCookie(cookies, name) == null) return false;
            return true;
        }

        private static Cookie FindCookie(CookieContainer cookies, string name)
        {
            if (cookies == null) return null;
            foreach (Uri origin in CookieOrigins)
            {
                Cookie value = cookies.GetCookies(origin)[name];
                if (value != null && !value.Expired && !string.IsNullOrWhiteSpace(value.Value)) return value;
            }
            return null;
        }

        // Set-Cookie is authoritative. Callback parameters fill missing names only.
        private static CookieContainer MergeCookies(string callback, CookieContainer headers)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            Uri address;
            if (Uri.TryCreate(callback, UriKind.Absolute, out address) && address.Scheme == "https"
                && (address.Host == "bilibili.com" || address.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase)
                    || address.Host == "biligame.com" || address.Host.EndsWith(".biligame.com", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (string pair in address.Query.TrimStart('?').Split('&'))
                {
                    int split = pair.IndexOf('=');
                    if (split <= 0) continue;
                    string name = Uri.UnescapeDataString(pair.Substring(0, split));
                    // Keep percent escapes and base64 '=' padding exactly as supplied.
                    parameters[name] = pair.Substring(split + 1);
                }
            }
            string expiryText;
            DateTime? expiry = parameters.TryGetValue("Expires", out expiryText) ? ParseExpiry(expiryText) : null;
            var result = new CookieContainer();
            foreach (string name in CookieNames)
            {
                Cookie source = FindCookie(headers, name);
                string value = null;
                if (source == null && (!parameters.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))) continue;
                else if (source != null) value = source.Value;
                try
                {
                    var cookie = new Cookie(name, value, "/", ".bilibili.com") { Secure = true, HttpOnly = source?.HttpOnly ?? true };
                    if (source != null && source.Expires != DateTime.MinValue) cookie.Expires = source.Expires;
                    else if (source == null && expiry.HasValue) cookie.Expires = expiry.Value;
                    if (!cookie.Expired) result.Add(cookie);
                }
                catch (CookieException) { }
            }
            return result;
        }

        private static DateTime? ParseExpiry(string raw)
        {
            string text = Uri.UnescapeDataString(raw.Replace("+", " "));
            long number;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                if (number <= 0) return null;
                try { return number >= 1000000000 ? DateTimeOffset.FromUnixTimeSeconds(number).UtcDateTime : DateTime.UtcNow.AddSeconds(number); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            DateTimeOffset expiry;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out expiry)
                ? (DateTime?)expiry.UtcDateTime : null;
        }

        private static void EnsureSuccess(JObject document)
        {
            int code;
            if (!int.TryParse((string)document["code"], NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                throw new InvalidOperationException("登录接口响应无效。");
            if (code != 0) throw new InvalidOperationException("登录接口暂不可用（" + code + "），请稍后重试。");
        }

        private static async Task<LoginResponse> RequestAsync(string url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var cookies = new CookieContainer();
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.CookieContainer = cookies;
            request.Timeout = request.ReadWriteTimeout = 20000;
            request.Method = "GET";
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/132.0 Safari/537.36";
            request.Referer = "https://www.bilibili.com/";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using (timeout.Token.Register(request.Abort))
                {
                    try
                    {
                        using (var response = await request.GetResponseAsync().ConfigureAwait(false))
                        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        {
                            string text = await reader.ReadToEndAsync().ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (timeout.IsCancellationRequested) throw new TimeoutException("登录请求超时，请检查网络后重试。");
                            try { return new LoginResponse { Document = JObject.Parse(text), Cookies = cookies }; }
                            catch (JsonException) { throw new InvalidOperationException("登录接口未返回有效数据，请检查网络或稍后重试。"); }
                        }
                    }
                    catch (Exception error) when (error is WebException || error is IOException || error is ObjectDisposedException)
                    {
                        token.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested) throw new TimeoutException("登录请求超时，请检查网络后重试。");
                        throw new InvalidOperationException("无法连接哔哩哔哩登录服务，请检查网络或稍后重试。");
                    }
                }
            }
        }

        private sealed class LoginResponse { public JObject Document; public CookieContainer Cookies; }
        private sealed class SavedSession { public int Version { get; set; } public List<CookieRecord> Cookies { get; set; } }
        private sealed class CookieRecord { public string Name { get; set; } public string Value { get; set; } public bool HttpOnly { get; set; } public DateTime? ExpiresUtc { get; set; } }
    }
}
