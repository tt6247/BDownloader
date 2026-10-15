
using BDownloader.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BDownloader.Services
{
    // Only the explicitly imported YouTube domains are retained. Credentials never
    // enter the queue/history JSON or the download log.
    public static class MediaAccounts
    {
        private static string Store => Path.Combine(AppPaths.DataDirectory, "Config", "MediaAccounts");
        private static string AccountFile => Path.Combine(Store, "youtube.bin");
        public static bool HasYouTube => File.Exists(AccountFile);

        public static void ImportYouTube(string file)
        {
            var accepted = new List<string>();
            foreach (string line in File.ReadAllLines(file))
            {
                string value = line.StartsWith("#HttpOnly_", StringComparison.Ordinal) ? line.Substring(10) : line;
                if (value.StartsWith("#") || string.IsNullOrWhiteSpace(value)) continue;
                string[] fields = value.Split('\t');
                if (fields.Length != 7) throw new InvalidDataException("请导入 Netscape 格式的 cookies.txt（7 列，以制表符分隔）。");
                string domain = fields[0].TrimStart('.').ToLowerInvariant();
                if (domain != "youtube.com" && !domain.EndsWith(".youtube.com", StringComparison.Ordinal)) continue;
                long expiry;
                if (!long.TryParse(fields[4], out expiry)) throw new InvalidDataException("Cookie 有效期格式错误。");
                if (expiry != 0 && expiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) continue;
                accepted.Add(line);
            }
            if (accepted.Count == 0) throw new InvalidDataException("文件中没有有效的 YouTube Cookie，请重新登录后导出。");
            Directory.CreateDirectory(Store);
            byte[] protectedData = ProtectedData.Protect(Encoding.UTF8.GetBytes("# Netscape HTTP Cookie File\n" + string.Join("\n", accepted) + "\n"), null, DataProtectionScope.CurrentUser);
            string temporary = AccountFile + ".tmp";
            File.WriteAllBytes(temporary, protectedData);
            if (File.Exists(AccountFile)) File.Replace(temporary, AccountFile, null);
            else File.Move(temporary, AccountFile);
        }

        public static void ClearYouTube() { if (File.Exists(AccountFile)) File.Delete(AccountFile); }

        public sealed class SessionLease : IDisposable
        {
            public MediaSession Session { get; private set; }
            private readonly string file;
            internal SessionLease(string path) { file = path; Session = new MediaSession { CookiesFile = path }; }
            public void Dispose()
            {
                try { if (file != null && File.Exists(file)) File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public static SessionLease CreateSession(string url)
        {
            var text = new StringBuilder("# Netscape HTTP Cookie File\n");
            bool hasCookies = false;
            Uri address;
            if (Uri.TryCreate(url, UriKind.Absolute, out address) &&
                (address.Host == "bilibili.com" || address.Host.EndsWith(".bilibili.com", StringComparison.OrdinalIgnoreCase) || address.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase)))
            {
                CookieContainer container = BilibiliLogin.GetCookies();
                if (container != null)
                    foreach (Cookie cookie in container.GetCookies(new Uri("https://www.bilibili.com/")))
                    {
                        if (cookie.Expired) continue;
                        long expires = cookie.Expires == DateTime.MinValue ? 0 : new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds();
                        text.AppendFormat(".bilibili.com\tTRUE\t{0}\t{1}\t{2}\t{3}\t{4}\n", cookie.Path, cookie.Secure ? "TRUE" : "FALSE", expires, cookie.Name, cookie.Value);
                        hasCookies = true;
                    }
            }
            else if (address != null && (address.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) || address.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) && HasYouTube)
            {
                text.Clear();
                text.Append(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(AccountFile), null, DataProtectionScope.CurrentUser)));
                hasCookies = true;
            }
            if (!hasCookies) return new SessionLease(null);
            Directory.CreateDirectory(Store);
            CleanupSessions();
            string path = Path.Combine(Store, "session-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N") + ".txt");
            // A private ACL limits temporary plaintext to the current Windows user.
            var security = new System.Security.AccessControl.FileSecurity();
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text.ToString());
                return new SessionLease(path);
            }
            catch { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } throw; }
        }

        public static void CleanupSessions()
        {
            if (!Directory.Exists(Store)) return;
            foreach (string file in Directory.GetFiles(Store, "session-*.txt"))
            {
                string[] parts = Path.GetFileNameWithoutExtension(file).Split('-');
                int processId;
                if (parts.Length != 3 || !int.TryParse(parts[1], out processId)) continue;
                bool running;
                try { using (var process = System.Diagnostics.Process.GetProcessById(processId)) running = !process.HasExited; }
                catch (ArgumentException) { running = false; }
                catch (System.ComponentModel.Win32Exception) { continue; }
                if (!running) { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
    }
}
