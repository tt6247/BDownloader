using System;
using System.IO;

namespace BDownloader.Services
{
    /// <summary>
    /// Runtime files belong to the executing package. The single-file launcher supplies
    /// a stable data directory so extracted runtime versions never own account or queue data.
    /// Without an override, directory installations retain their existing local data layout.
    /// </summary>
    public static class AppPaths
    {
        public const string DataDirectoryVariable = "BDOWNLOADER_DATA_DIRECTORY";

        public static string RuntimeDirectory => AppDomain.CurrentDomain.BaseDirectory;

        public static string DataDirectory
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable(DataDirectoryVariable);
                if (string.IsNullOrEmpty(configured)) return RuntimeDirectory;
                if (!IsFullyQualified(configured)) throw InvalidOverride();
                try { return Path.GetFullPath(configured); }
                catch (ArgumentException) { throw InvalidOverride(); }
                catch (NotSupportedException) { throw InvalidOverride(); }
                catch (PathTooLongException) { throw InvalidOverride(); }
            }
        }

        private static bool IsFullyQualified(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (char.IsControl(c) || c == '"' || c == '<' || c == '>' || c == '|' || c == '*' || c == '?'
                    || (c == ':' && index != 1)) return false;
            }
            bool driveLetter = value.Length > 0 && ((value[0] >= 'A' && value[0] <= 'Z') || (value[0] >= 'a' && value[0] <= 'z'));
            if (value.Length >= 3 && driveLetter && value[1] == ':' && IsSeparator(value[2])) return true;
            if (value.Length < 5 || !IsSeparator(value[0]) || !IsSeparator(value[1])) return false;
            string[] parts = value.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && parts[0] != "." && parts[0] != ".." && parts[1] != "." && parts[1] != "..";
        }

        private static bool IsSeparator(char value) => value == '\\' || value == '/';

        private static InvalidOperationException InvalidOverride() => new InvalidOperationException(
            DataDirectoryVariable + " must be a fully qualified directory path without control characters or device namespace prefixes.");
    }
}
