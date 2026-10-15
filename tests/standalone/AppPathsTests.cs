using System;
using System.IO;
using System.Reflection;
using BDownloader.Services;

internal static class AppPathsTests
{
    private static int failures;
    private static readonly string Variable = "BDOWNLOADER_DATA_DIRECTORY";

    public static int Main()
    {
        string previous = Environment.GetEnvironmentVariable(Variable);
        string syntheticRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "path-fixture-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(syntheticRoot, "stable-data");
        try
        {
            Check("directory mode preserves application-local data", () => {
                Environment.SetEnvironmentVariable(Variable, null);
                Require(AppPaths.RuntimeDirectory == AppDomain.CurrentDomain.BaseDirectory, "runtime path changed");
                Require(AppPaths.DataDirectory == AppDomain.CurrentDomain.BaseDirectory, "existing folder-mode data location changed");
            });
            Check("data override is canonical and creates no directory", () => {
                Environment.SetEnvironmentVariable(Variable, Path.Combine(syntheticRoot, "unused", "..", "stable-data"));
                Require(Same(AppPaths.DataDirectory, data), "override was not normalized");
                Require(!Directory.Exists(syntheticRoot), "path lookup created storage directories");
                Require(AppPaths.RuntimeDirectory == AppDomain.CurrentDomain.BaseDirectory, "data override changed executable lookup");
            });
            Check("runtime version changes keep an overridden data directory stable", () => {
                Environment.SetEnvironmentVariable(Variable, data);
                string[] first = ReadInDomain(Path.Combine(syntheticRoot, "runtime-v1", "payload"));
                string[] second = ReadInDomain(Path.Combine(syntheticRoot, "runtime-v2", "payload"));
                Require(!Same(first[0], second[0]), "test domains did not change runtime paths");
                Require(Same(first[1], data) && Same(second[1], data), "versioned runtime changed persistent data path");
                Require(!Directory.Exists(syntheticRoot), "domain/path lookup wrote to runtime or data paths");
            });
            Check("directory-mode data follows each independent application base", () => {
                Environment.SetEnvironmentVariable(Variable, null);
                string[] first = ReadInDomain(Path.Combine(syntheticRoot, "portable-a"));
                string[] second = ReadInDomain(Path.Combine(syntheticRoot, "portable-b"));
                Require(Same(first[0], first[1]) && Same(second[0], second[1]), "directory mode no longer uses local storage");
                Require(!Same(first[1], second[1]), "independent directory installations share data unexpectedly");
            });
            Check("relative drive-relative and control-character overrides are rejected", () => {
                foreach (string invalid in new[] { "Data", "..\\Data", "C:Data", "\\Data", "/Data", "   ", "C:\\bad\nData", "C:\\bad\tData", "C:\\bad|Data", "C:\\bad*Data", "\"C:\\Data\"", "\\\\?\\C:\\Data", "\\\\.\\pipe\\Data", "\\\\server-only" })
                {
                    Environment.SetEnvironmentVariable(Variable, invalid);
                    bool rejected = false;
                    try { string ignored = AppPaths.DataDirectory; }
                    catch (InvalidOperationException) { rejected = true; }
                    Require(rejected, "invalid override was accepted or silently used the runtime directory");
                }
                // Windows environment strings cannot carry embedded NULs; exercise the
                // same validator directly rather than testing OS truncation behavior.
                var validator = typeof(AppPaths).GetMethod("IsFullyQualified", BindingFlags.NonPublic | BindingFlags.Static);
                Require(!(bool)validator.Invoke(null, new object[] { "C:\\bad\0Data" }), "NUL accepted by path validation");
            });
            Check("normal absolute drive and UNC paths are accepted without I/O", () => {
                foreach (string absolute in new[] { data, "C:\\BDownloader data\\Data", "\\\\example.invalid\\share\\BDownloader\\Data" })
                {
                    Environment.SetEnvironmentVariable(Variable, absolute);
                    Require(Same(AppPaths.DataDirectory, absolute), "fully qualified override rejected");
                }
                Require(!Directory.Exists(syntheticRoot), "validating paths created directories");
            });
        }
        finally { Environment.SetEnvironmentVariable(Variable, previous); }
        Console.WriteLine("RESULT failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    private static string[] ReadInDomain(string applicationBase)
    {
        var setup = new AppDomainSetup { ApplicationBase = applicationBase };
        AppDomain domain = AppDomain.CreateDomain("AppPaths-" + Guid.NewGuid().ToString("N"), null, setup);
        try
        {
            var probe = (AppPathsProbe)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location, typeof(AppPathsProbe).FullName);
            return probe.Read();
        }
        finally { AppDomain.Unload(domain); }
    }
    private static bool Same(string first, string second) => string.Equals(Path.GetFullPath(first).TrimEnd('\\', '/'), Path.GetFullPath(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Check(string name, Action test)
    { try { test(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.GetBaseException().Message); } }
}

public sealed class AppPathsProbe : MarshalByRefObject
{
    public string[] Read() => new[] { AppPaths.RuntimeDirectory, AppPaths.DataDirectory };
    public override object InitializeLifetimeService() => null;
}
