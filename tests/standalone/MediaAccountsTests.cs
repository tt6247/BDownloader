using BDownloader.Services;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

// Compile this host together with Services/MediaAccounts.cs into a fresh child
// of analysis/verification/auth-sandbox. Only synthetic cookies are created.
internal static class MediaAccountsTests
{
    private static string sandbox;
    private static string fixtures;
    private static string store;
    private static string accountFile;
    private static int failures;

    public static int Main()
    {
        sandbox = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        if (sandbox.IndexOf(Path.Combine("analysis", "verification", "auth-sandbox") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.Error.WriteLine("Refusing to run outside a dedicated auth-sandbox.");
            return 2;
        }
        Environment.CurrentDirectory = sandbox;
        fixtures = Path.Combine(sandbox, "fixtures");
        store = Path.Combine(sandbox, "Config", "MediaAccounts");
        accountFile = Path.Combine(store, "youtube.bin");
        if (File.Exists(accountFile))
        {
            Console.Error.WriteLine("Refusing to touch preexisting account data; create a fresh sandbox.");
            return 2;
        }
        Directory.CreateDirectory(fixtures);
        return Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        Check("Netscape import filters domains and expired rows", () =>
        {
            long future = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600;
            string source = Write("mixed.txt", "# Netscape HTTP Cookie File\n"
                + Cookie(".youtube.com", "SID", "fake-youtube-session", future)
                + "#HttpOnly_" + Cookie(".youtube.com", "HSID", "fake-http-only", future)
                + Cookie(".m.youtube.com", "MOBILE", "fake-mobile", future)
                + Cookie(".youtube.com", "SESSION", "fake-session-cookie", 0)
                + Cookie(".google.com", "SID", "forbidden-google", future)
                + Cookie("youtube.com.attacker.example", "SID", "forbidden-spoofed", future)
                + Cookie(".youtube.com", "OLD", "forbidden-expired", 1));
            MediaAccounts.ImportYouTube(source);
            Require(MediaAccounts.HasYouTube, "import did not save a session");
            string decoded = ReadProtectedAccount();
            Require(decoded.Contains("fake-youtube-session") && decoded.Contains("#HttpOnly_.youtube.com") && decoded.Contains("fake-mobile") && decoded.Contains("fake-session-cookie"), "allowed cookies changed or disappeared");
            Require(!decoded.Contains("forbidden-"), "unrelated or expired cookies were retained");
            Require(File.ReadAllText(source).Contains("forbidden-google"), "import modified the source file");
        });

        Check("stored session is protected with Windows DPAPI", () =>
        {
            byte[] encrypted = File.ReadAllBytes(accountFile);
            Require(encrypted.Length > 32, "encrypted session is empty");
            Require(!Encoding.UTF8.GetString(encrypted).Contains("fake-youtube-session"), "stored credentials are plaintext");
            Require(ReadProtectedAccount().Contains("fake-youtube-session"), "DPAPI round trip failed");
            Require(!File.Exists(accountFile + ".tmp"), "import temporary file was left behind");
        });

        Check("temporary cookies have private ACL and are deleted after disposal", () =>
        {
            string temporary;
            using (var lease = MediaAccounts.CreateSession("https://www.youtube.com/watch?v=fixture"))
            {
                temporary = lease.Session.CookiesFile;
                Require(!string.IsNullOrWhiteSpace(temporary) && File.Exists(temporary), "session file was not created");
                Require(Path.GetFullPath(temporary).StartsWith(store + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "session escaped sandbox store");
                Require(File.ReadAllText(temporary).Contains("fake-youtube-session"), "session data does not match import");
                FileSecurity acl = File.GetAccessControl(temporary);
                Require(acl.AreAccessRulesProtected, "temporary credentials inherit a public ACL");
                SecurityIdentifier owner = WindowsIdentity.GetCurrent().User;
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    Require(rule.IdentityReference.Equals(owner), "temporary credentials grant access to another account");
            }
            Require(!File.Exists(temporary), "session was not deleted on Dispose");
            Require(!Directory.GetFiles(store, "session-*.txt").Any(), "plaintext session was left behind");
        });

        Check("simultaneous leases are independent", () =>
        {
            using (var first = MediaAccounts.CreateSession("https://youtu.be/fixture"))
            using (var second = MediaAccounts.CreateSession("https://m.youtube.com/watch?v=fixture"))
            {
                Require(first.Session.CookiesFile != second.Session.CookiesFile, "parallel operations share a mutable cookie file");
                first.Dispose();
                Require(File.Exists(second.Session.CookiesFile), "disposing one lease removed another lease");
            }
            Require(!Directory.GetFiles(store, "session-*.txt").Any(), "one of the session files was left behind");
        });

        Check("orphaned sessions are cleaned without deleting an active process session", () =>
        {
            string stale = Path.Combine(store, "session-2147483647-" + Guid.NewGuid().ToString("N") + ".txt");
            string active = Path.Combine(store, "session-" + System.Diagnostics.Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(stale, "fake-orphaned-session");
            File.WriteAllText(active, "fake-active-session");
            try
            {
                MediaAccounts.CleanupSessions();
                Require(!File.Exists(stale), "orphaned plaintext session remains");
                Require(File.Exists(active), "an active process lost its session");
            }
            finally
            {
                if (File.Exists(stale)) File.Delete(stale);
                if (File.Exists(active)) File.Delete(active);
            }
        });

        Check("Bilibili short URLs never receive a YouTube session", () =>
        {
            using (var lease = MediaAccounts.CreateSession("https://b23.tv/fixture"))
                Require(lease.Session.CookiesFile == null, "YouTube credentials were used for Bilibili short URL");
        });

        Check("unsupported and spoofed domains never receive stored credentials", () =>
        {
            foreach (string url in new[] { "https://example.com/", "https://youtube.com.attacker.example/watch?v=fixture", "not a URL" })
                using (var lease = MediaAccounts.CreateSession(url))
                    Require(lease.Session.CookiesFile == null, "credentials exported for unsupported URL");
        });

        Check("invalid Netscape input preserves the existing protected session", () =>
        {
            string before = Hash(accountFile);
            foreach (string contents in new[]
            {
                "SID=fake-not-netscape",
                Cookie(".google.com", "SID", "fake-wrong-domain", 0),
                Cookie(".youtube.com", "SID", "fake-expired", 1),
                ".youtube.com\tTRUE\t/\tTRUE\tnot-a-number\tSID\tfake-invalid-expiry\n",
                "# Netscape HTTP Cookie File\n"
            })
            {
                bool rejected = false;
                try { MediaAccounts.ImportYouTube(Write("invalid-" + Guid.NewGuid().ToString("N") + ".txt", contents)); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "invalid import was accepted");
                Require(Hash(accountFile) == before, "invalid input replaced the existing session");
            }
        });

        Check("missing source preserves the stored session", () =>
        {
            string before = Hash(accountFile);
            try { MediaAccounts.ImportYouTube(Path.Combine(fixtures, "missing.txt")); throw new Exception("missing source was accepted"); }
            catch (FileNotFoundException) { }
            Require(Hash(accountFile) == before, "missing source removed stored session");
        });

        Check("logout deletes the protected account and public downloads are anonymous", () =>
        {
            MediaAccounts.ClearYouTube();
            Require(!MediaAccounts.HasYouTube && !File.Exists(accountFile), "logout did not remove stored session");
            using (var lease = MediaAccounts.CreateSession("https://www.youtube.com/watch?v=fixture"))
                Require(lease.Session.CookiesFile == null, "logged-out operation still exports credentials");
            Require(!Directory.GetFiles(store, "session-*.txt").Any(), "plaintext sessions remain after logout");
        });

        Console.WriteLine("RESULT failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static string Cookie(string domain, string name, string value, long expiry)
    {
        return domain + "\tTRUE\t/\tTRUE\t" + expiry + "\t" + name + "\t" + value + "\n";
    }

    private static string Write(string name, string contents)
    {
        string path = Path.Combine(fixtures, name);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
        return path;
    }

    private static string ReadProtectedAccount()
    {
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(accountFile), null, DataProtectionScope.CurrentUser));
    }

    private static string Hash(string path)
    {
        using (SHA256 sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
}
