using BDownloader.Services;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// Uses only synthetic credentials in a fresh, dedicated application directory.
internal static class BilibiliLoginTests
{
    private const string Callback = "https://passport.biligame.com/crossDomain?DedeUserID=424242&SESSDATA=fake%2Csession==&bili_jct=fake-csrf";
    private static readonly Uri Origin = new Uri("https://api.bilibili.com/");
    private static int failures;
    private static string account;
    private static CookieContainer session;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string sandbox = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        if (sandbox.IndexOf(Path.Combine("analysis", "verification", "auth-sandbox") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
        {
            Console.Error.WriteLine("Refusing to run outside a dedicated auth-sandbox.");
            return 2;
        }
        Environment.CurrentDirectory = sandbox;
        account = Path.Combine(sandbox, "Config", "Accounts", "bilibili.bin");
        if (File.Exists(account))
        {
            Console.Error.WriteLine("Refusing to touch preexisting account data; use a fresh sandbox.");
            return 2;
        }
        return Run(args.Contains("--online"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(bool online)
    {
        try
        {
            Check("callback preserves percent escapes and equals padding", () =>
            {
                session = Parse(Callback, null);
                Require(session.GetCookies(Origin)["SESSDATA"]?.Value == "fake%2Csession==", "callback value changed");
            });
            Check("omitted Expires remains a session cookie", () =>
            {
                Require(session.GetCookies(Origin)["SESSDATA"].Expires == DateTime.MinValue, "an expiry was invented");
            });
            Check("relative expiration is applied", () =>
            {
                CookieContainer parsed = Parse(Callback + "&Expires=3600", null);
                DateTime expiry = parsed.GetCookies(Origin)["SESSDATA"].Expires.ToUniversalTime();
                Require(expiry > DateTime.UtcNow.AddMinutes(59) && expiry < DateTime.UtcNow.AddMinutes(61), "relative expiry is incorrect");
            });
            Check("full HTTP date and Unix expiration are supported", () =>
            {
                DateTimeOffset tomorrow = DateTimeOffset.UtcNow.AddDays(1);
                string fullDate = Uri.EscapeDataString(tomorrow.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
                foreach (string value in new[] { fullDate, tomorrow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) })
                {
                    CookieContainer parsed = Parse(Callback + "&Expires=" + value, null);
                    Require(parsed.GetCookies(Origin)["SESSDATA"].Expires.ToUniversalTime() > DateTime.UtcNow.AddHours(23), "absolute expiry is incorrect");
                }
            });
            Check("response headers win while callback fills missing fields", () =>
            {
                var headers = new CookieContainer();
                headers.Add(new Cookie("SESSDATA", "fake-authoritative", "/", ".bilibili.com"));
                CookieContainer merged = Parse(Callback, headers);
                Require(merged.GetCookies(Origin)["SESSDATA"]?.Value == "fake-authoritative", "header cookie did not win");
                Require(merged.GetCookies(Origin)["bili_jct"]?.Value == "fake-csrf", "callback fallback is missing");
            });
            Check("synthetic session saves through standalone DPAPI service", () =>
            {
                Require(BilibiliLogin.SaveCookies(session) && BilibiliLogin.HasSession, "session save failed");
            });
            Check("standalone DPAPI roundtrip preserves cookie values", () =>
            {
                CookieContainer loaded = BilibiliLogin.GetCookies();
                Require(loaded?.GetCookies(Origin)["SESSDATA"]?.Value == "fake%2Csession==", "saved session did not roundtrip");
                Require(loaded.GetCookies(Origin)["SESSDATA"].Expires == DateTime.MinValue, "session cookie acquired an expiry");
            });
            Check("persistent credentials are encrypted with no leftover temporary file", () =>
            {
                byte[] bytes = File.ReadAllBytes(account);
                Require(bytes.Length > 32 && !Encoding.UTF8.GetString(bytes).Contains("fake-csrf"), "stored credentials are plaintext");
                Require(!Directory.GetFiles(Path.GetDirectoryName(account), "bilibili.bin.tmp-*").Any(), "temporary account file remains");
            });
            Check("incomplete login cannot replace the saved session", () =>
            {
                string before = Convert.ToBase64String(File.ReadAllBytes(account));
                Require(!BilibiliLogin.SaveCookies(new CookieContainer()), "incomplete credentials were accepted");
                Require(Convert.ToBase64String(File.ReadAllBytes(account)) == before, "invalid login replaced saved data");
            });
            Check("pre-cancelled QR request avoids the network", () =>
            {
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    bool cancelled = false;
                    try { BilibiliLogin.GenerateAsync(cancellation.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Require(cancelled, "cancellation was ignored");
                }
            });
            Check("standalone logout removes the saved synthetic session", () =>
            {
                BilibiliLogin.Logout();
                Require(!BilibiliLogin.HasSession && !File.Exists(account), "logout retained the account");
            });
            if (online)
            {
                Check("anonymous QR generation and unscanned polling", () =>
                {
                    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                    {
                        QrLoginRequest request = BilibiliLogin.GenerateAsync(cancellation.Token).GetAwaiter().GetResult();
                        Uri url;
                        Require(!string.IsNullOrWhiteSpace(request.Key) && Uri.TryCreate(request.Url, UriKind.Absolute, out url) && url.Scheme == "https", "QR metadata is invalid");
                        QrLoginResult result = BilibiliLogin.PollAsync(request.Key, cancellation.Token).GetAwaiter().GetResult();
                        Require(result.Code == 86101, "expected unscanned code 86101; actual=" + result.Code);
                        Require(!BilibiliLogin.HasSession && !File.Exists(account), "network check unexpectedly saved an account");
                    }
                });
            }
            else Console.WriteLine("SKIP QR network check; pass --online to enable");
        }
        finally
        {
            // The guard above proves this can only remove our synthetic fixture.
            if (File.Exists(account)) BilibiliLogin.Logout();
        }
        Console.WriteLine("RESULT failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static CookieContainer Parse(string callback, CookieContainer headers)
    {
        MethodInfo merge = typeof(BilibiliLogin).GetMethod("MergeCookies", BindingFlags.Static | BindingFlags.NonPublic);
        Require(merge != null, "callback parser was not found");
        return (CookieContainer)merge.Invoke(null, new object[] { callback, headers });
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Check(string name, Action test)
    {
        try { test(); Console.WriteLine("PASS " + name); }
        catch (Exception error)
        {
            failures++;
            string message = Regex.Replace(error.GetBaseException().Message, @"https?://[^\s]+", "[URL omitted]");
            Console.WriteLine("FAIL " + name + ": " + message.Replace('\r', ' ').Replace('\n', ' '));
        }
    }
}
