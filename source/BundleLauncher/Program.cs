using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace BDownloader.BundleLauncher
{
    internal static class Program
    {
        private const string ManifestName = "bundle-manifest.json";
        private static readonly HashSet<string> RuntimeNames = new HashSet<string>(StringComparer.Ordinal) {
            "BDownloader.exe", "BDownloader.exe.config", "Newtonsoft.Json.dll", "QRCoder.dll", "ffmpeg.exe",
            "FFmpeg_LICENSE.txt", "yt-dlp.exe", "qjs.exe", "quickjs-runtime.json", "media-tools.json", "LICENSE.txt", "aria2c.exe"
        };

        [STAThread]
        private static int Main(string[] args)
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
            bool verifyOnly = args.Length > 0 && args[0] == "--bundle-verify";
            string reportPath = null;
            try
            {
                if (verifyOnly && args.Length == 2) reportPath = Path.GetFullPath(args[1]);
                if (verifyOnly && args.Length > 2) throw new ArgumentException("Usage: --bundle-verify [report.json]");
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local)) throw new IOException("The local application data directory is unavailable.");
                string cache = ResolveRoot("BDOWNLOADER_CACHE_DIRECTORY", Path.Combine(local, "BDownloader", "Runtime"));
                string data = ResolveRoot("BDOWNLOADER_DATA_DIRECTORY", Path.Combine(local, "BDownloader", "Data"));
                if (InsideOrEqual(cache, data) || InsideOrEqual(data, cache))
                    throw new IOException("Runtime cache and persistent data directories must be separate and cannot overlap.");
                EnsureDirectory(cache);
                EnsureDirectory(data);
                string final = SafeChild(cache, BundleInfo.PayloadSha256);
                bool extracted = false;
                BundleManifest manifest;
                string manifestHash;

                using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("BDownloader.Payload.zip"))
                {
                    if (payload == null || payload.Length != BundleInfo.PayloadZipLength || Hash(payload) != BundleInfo.PayloadSha256)
                        throw new InvalidDataException("The embedded application payload failed its SHA-256 check.");
                    payload.Position = 0;
                    using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, true))
                    {
                        byte[] manifestBytes;
                        manifest = ReadManifest(archive, out manifestBytes);
                        using (var stream = new MemoryStream(manifestBytes, false)) manifestHash = Hash(stream);
                        ValidateArchive(archive, manifest);
                        bool owned = false;
                        using (Mutex mutex = CreateMutex())
                        {
                            try
                            {
                                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                                catch (AbandonedMutexException) { owned = true; }
                                if (!owned) throw new TimeoutException("Another BDownloader launch is preparing its files. Please try again.");
                                string reason;
                                if (!VerifyDirectory(final, manifest, manifestHash, out reason))
                                {
                                    if (Directory.Exists(final))
                                    {
                                        RejectReparse(final);
                                        Directory.Move(final, SafeChild(cache, ".invalid-" + BundleInfo.PayloadSha256.Substring(0, 12) + "-" + Guid.NewGuid().ToString("N").Substring(0, 12)));
                                    }
                                    if (File.Exists(final)) throw new IOException("A file occupies the versioned runtime directory.");
                                    string stage = SafeChild(cache, ".extract-" + BundleInfo.PayloadSha256.Substring(0, 12) + "-" + Guid.NewGuid().ToString("N").Substring(0, 12));
                                    EnsureDirectory(stage);
                                    Extract(archive, stage);
                                    if (!VerifyDirectory(stage, manifest, manifestHash, out reason))
                                        throw new InvalidDataException("Extracted payload verification failed: " + reason);
                                    Directory.Move(stage, final);
                                    extracted = true;
                                }
                                if (!VerifyDirectory(final, manifest, manifestHash, out reason))
                                    throw new InvalidDataException("Runtime verification failed: " + reason);
                            }
                            finally { if (owned) mutex.ReleaseMutex(); }
                        }
                    }
                }

                string runtime = SafeChild(final, "payload");
                var report = new VerificationReport {
                    Success = true, PayloadSha256 = BundleInfo.PayloadSha256,
                    ApplicationSha256 = manifest.Files.Single(x => x.Path == "payload/BDownloader.exe").Sha256,
                    RuntimeDirectory = runtime, DataDirectory = data, FilesVerified = manifest.Files.Count,
                    AllHashesVerified = true, CacheWasExtracted = extracted, CacheReused = !extracted,
                    Extracted = extracted, VerifiedFileCount = manifest.Files.Count, LaunchedApplication = false,
                    VerifiedUtc = DateTime.UtcNow.ToString("o")
                };
                if (verifyOnly)
                {
                    if (reportPath == null) reportPath = Path.Combine(data, "bundle-verification.json");
                    WriteReport(reportPath, report);
                    Console.WriteLine("Bundle verified: " + reportPath);
                    return 0;
                }
                var start = new ProcessStartInfo {
                    FileName = SafeChild(runtime, "BDownloader.exe"), WorkingDirectory = data,
                    UseShellExecute = false, Arguments = string.Join(" ", args.Select(Quote))
                };
                start.EnvironmentVariables["BDOWNLOADER_DATA_DIRECTORY"] = data;
                start.EnvironmentVariables["BDOWNLOADER_RUNTIME_DIRECTORY"] = runtime;
                // The child is a normal GUI process. Closing this launcher does
                // not terminate it or any of its legitimate download workers.
                using (Process child = Process.Start(start))
                    if (child == null) throw new IOException("BDownloader could not be started.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                if (verifyOnly)
                {
                    if (reportPath != null)
                    {
                        try { WriteReport(reportPath, new VerificationReport { Success = false, Error = error.Message, VerifiedUtc = DateTime.UtcNow.ToString("o") }); }
                        catch (Exception) { }
                    }
                }
                else MessageBox.Show(error.Message, "BDownloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static BundleManifest ReadManifest(ZipArchive archive, out byte[] bytes)
        {
            ZipArchiveEntry entry = archive.GetEntry(ManifestName);
            if (entry == null || entry.Length <= 0 || entry.Length > 1024 * 1024) throw new InvalidDataException("Invalid embedded manifest.");
            using (var input = entry.Open())
            using (var memory = new MemoryStream()) { input.CopyTo(memory); bytes = memory.ToArray(); }
            BundleManifest manifest;
            using (var input = new MemoryStream(bytes, false))
                manifest = (BundleManifest)new DataContractJsonSerializer(typeof(BundleManifest)).ReadObject(input);
            if (manifest == null || manifest.SchemaVersion != 1 || manifest.Files == null || manifest.Files.Count != BundleInfo.ExpectedFileCount ||
                manifest.Files.Count < 5 || manifest.Files.Count > 64) throw new InvalidDataException("Unsupported embedded manifest.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BundleFile file in manifest.Files)
            {
                if (file == null || !AllowedPath(file.Path) || file.Path == ManifestName || !names.Add(file.Path) ||
                    file.Size < 0 || file.Size > 256L * 1024 * 1024 || file.Sha256 == null || !Regex.IsMatch(file.Sha256, "^[0-9a-f]{64}$"))
                    throw new InvalidDataException("Invalid file in the embedded manifest.");
            }
            foreach (string required in RuntimeNames)
                if (!names.Contains("payload/" + required)) throw new InvalidDataException("The embedded runtime is incomplete.");
            return manifest;
        }

        private static void ValidateArchive(ZipArchive archive, BundleManifest manifest)
        {
            if (archive.Entries.Count != manifest.Files.Count + 1) throw new InvalidDataException("Unexpected payload entry count.");
            var files = manifest.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!AllowedPath(entry.FullName) || !names.Add(entry.FullName)) throw new InvalidDataException("Unexpected payload path.");
                if (entry.FullName == ManifestName) continue;
                BundleFile expected;
                if (!files.TryGetValue(entry.FullName, out expected) || expected.Size != entry.Length) throw new InvalidDataException("Payload length mismatch.");
                total += entry.Length;
                if (total > 512L * 1024 * 1024) throw new InvalidDataException("The payload is too large.");
            }
        }

        private static bool AllowedPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith("/", StringComparison.Ordinal) ||
                path.Split('/').Any(x => x.Length == 0 || x == "." || x == "..")) return false;
            if (path == ManifestName) return true;
            if (!path.StartsWith("payload/", StringComparison.Ordinal)) return false;
            string name = path.Substring(8);
            if (RuntimeNames.Contains(name)) return true;
            if (!name.StartsWith("ThirdPartyLicenses/", StringComparison.Ordinal)) return false;
            name = name.Substring("ThirdPartyLicenses/".Length);
            if (name == "quickjs-release-proof.json") return true;
            return Regex.IsMatch(name, @"^(yt-dlp|FFmpeg|Newtonsoft\.Json|QRCoder|quickjs|qjs|aria2)[A-Za-z0-9_.-]*$", RegexOptions.IgnoreCase) &&
                Regex.IsMatch(name, @"(?i)(LICENSE|COPYING|NOTICE|SHA(?:2-)?256|sha256sum)") &&
                (Regex.IsMatch(name, @"(?i)\.(txt|md|sha256sum)$") || name.EndsWith("LICENSE", StringComparison.Ordinal) || name == "yt-dlp-SHA2-256SUMS");
        }

        private static void Extract(ZipArchive archive, string root)
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string target = SafeChild(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                EnsureDirectory(Path.GetDirectoryName(target));
                using (Stream input = entry.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            }
        }

        private static bool VerifyDirectory(string root, BundleManifest manifest, string manifestHash, out string reason)
        {
            reason = "The cache does not exist.";
            if (!Directory.Exists(root)) return false;
            RejectReparse(root);
            var expected = manifest.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
            var pending = new Queue<string>(); pending.Enqueue(root);
            int count = 0;
            try
            {
                while (pending.Count > 0)
                {
                    foreach (string path in Directory.GetFileSystemEntries(pending.Dequeue()))
                    {
                        RejectReparse(path);
                        string relative = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                        if (Directory.Exists(path))
                        {
                            if (relative != "payload" && relative != "payload/ThirdPartyLicenses") { reason = "Unexpected runtime directory."; return false; }
                            pending.Enqueue(path); continue;
                        }
                        if (!AllowedPath(relative)) { reason = "Unexpected runtime file."; return false; }
                        string hash; long size;
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { size = stream.Length; hash = Hash(stream); }
                        if (relative == ManifestName)
                        {
                            if (hash != manifestHash) { reason = "Cached manifest changed."; return false; }
                        }
                        else
                        {
                            BundleFile file;
                            if (!expected.TryGetValue(relative, out file) || size != file.Size || hash != file.Sha256) { reason = "Cached file changed: " + relative; return false; }
                        }
                        count++;
                    }
                }
                if (count != manifest.Files.Count + 1) { reason = "Cached files are missing."; return false; }
                reason = null; return true;
            }
            catch (UnsafePathException) { throw; }
            catch (IOException) { reason = "A cached file could not be read."; return false; }
            catch (UnauthorizedAccessException) { reason = "A cached file is inaccessible."; return false; }
        }

        private static Mutex CreateMutex()
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            if (user == null) throw new IOException("The current Windows user is unavailable.");
            var security = new MutexSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(user, MutexRights.FullControl, AccessControlType.Allow));
            bool created;
            return new Mutex(false, @"Local\BDownloader.Bundle." + user.Value + "." + BundleInfo.PayloadSha256, out created, security);
        }

        private static string ResolveRoot(string variable, string fallback)
        {
            string value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value)) value = fallback;
            if (!Regex.IsMatch(value, @"^[A-Za-z]:[\\/]") || value.Any(char.IsControl))
                throw new IOException(variable + " must be a fully qualified local drive directory.");
            string full = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length <= Path.GetPathRoot(full).Length) throw new IOException("A drive root cannot store application state.");
            RejectReparseChain(full);
            return full;
        }

        private static string SafeChild(string root, string relative)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(prefix, relative));
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A runtime path escaped its cache directory.");
            return target;
        }

        private static bool InsideOrEqual(string path, string parent)
        {
            return path.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureDirectory(string path)
        {
            RejectReparseChain(path);
            if (Directory.Exists(path)) return;
            Directory.CreateDirectory(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, security);
        }

        private static void RejectReparseChain(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) || File.Exists(current)) RejectReparse(current);
                current = Path.GetDirectoryName(current);
            }
        }

        private static void RejectReparse(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new UnsafePathException("A runtime directory cannot contain symbolic links or junctions.");
        }

        private static string Hash(Stream stream)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static void WriteReport(string path, VerificationReport report)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new DataContractJsonSerializer(typeof(VerificationReport)).WriteObject(output, report);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }

        private static string Quote(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { result.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                result.Append('\\', slashes).Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        private sealed class UnsafePathException : IOException
        {
            public UnsafePathException(string message) : base(message) { }
        }
    }

    [DataContract] internal sealed class BundleManifest
    {
        [DataMember] public int SchemaVersion { get; set; }
        [DataMember] public List<BundleFile> Files { get; set; }
    }
    [DataContract] internal sealed class BundleFile
    {
        [DataMember] public string Path { get; set; }
        [DataMember] public long Size { get; set; }
        [DataMember] public string Sha256 { get; set; }
    }
    [DataContract] internal sealed class VerificationReport
    {
        [DataMember] public bool Success { get; set; }
        [DataMember] public string Error { get; set; }
        [DataMember] public string PayloadSha256 { get; set; }
        [DataMember] public string ApplicationSha256 { get; set; }
        [DataMember] public string RuntimeDirectory { get; set; }
        [DataMember] public string DataDirectory { get; set; }
        [DataMember] public int FilesVerified { get; set; }
        [DataMember] public bool AllHashesVerified { get; set; }
        [DataMember] public bool CacheWasExtracted { get; set; }
        [DataMember] public bool CacheReused { get; set; }
        [DataMember] public bool Extracted { get; set; }
        [DataMember] public int VerifiedFileCount { get; set; }
        [DataMember] public bool LaunchedApplication { get; set; }
        [DataMember] public string VerifiedUtc { get; set; }
    }
}
