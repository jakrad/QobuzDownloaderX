using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace QobuzDownloaderX.Helpers
{
    internal static class SecurityHelpers
    {
        private static readonly Regex querySecretRegex = new Regex(
            @"(?i)(?<key>app_secret|app_id|user_auth_token|password|token|request_sig|authorization|email)=(?<value>[^&\s]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex jsonSecretRegex = new Regex(
            "(?i)(?<prefix>\"(?:app_secret|app_id|user_auth_token|password|token|request_sig|authorization|email)\"\\s*:\\s*\")(?<value>[^\"]*)(?<suffix>\")",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex labeledSecretRegex = new Regex(
            @"(?im)(?<label>\b(?:App secret|App ID|User auth token|Password|Email|Authorization)\b\s*:\s*)(?<value>.+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex bearerTokenRegex = new Regex(
            @"(?i)(?<prefix>\bBearer\s+)(?<value>[A-Za-z0-9\-._~+/=]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static string RedactSensitiveData(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            string redacted = querySecretRegex.Replace(input, match => $"{match.Groups["key"].Value}=<redacted>");
            redacted = jsonSecretRegex.Replace(redacted, match => $"{match.Groups["prefix"].Value}<redacted>{match.Groups["suffix"].Value}");
            redacted = labeledSecretRegex.Replace(redacted, match => $"{match.Groups["label"].Value}<redacted>");
            redacted = bearerTokenRegex.Replace(redacted, match => $"{match.Groups["prefix"].Value}<redacted>");

            return redacted;
        }

        internal static string MaskValue(string value, int visibleSuffixLength = 4)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (visibleSuffixLength < 0)
            {
                visibleSuffixLength = 0;
            }

            if (value.Length <= visibleSuffixLength)
            {
                return new string('*', value.Length);
            }

            int maskedLength = Math.Max(4, value.Length - visibleSuffixLength);
            return new string('*', maskedLength) + value.Substring(value.Length - visibleSuffixLength);
        }

        internal static Uri RequireHttpsUrl(string url, string context, Func<Uri, bool> additionalValidator = null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                throw new InvalidOperationException($"{context} URL is invalid.");
            }

            if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{context} URL must use HTTPS.");
            }

            if (additionalValidator != null && !additionalValidator(uri))
            {
                throw new InvalidOperationException($"{context} URL host is not allowed.");
            }

            return uri;
        }

        internal static bool IsAllowedGitHubContentHost(Uri uri)
        {
            if (uri == null)
            {
                return false;
            }

            string host = uri.Host ?? string.Empty;

            return host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("media.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || host.Equals("user-images.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        internal static string EnsureTrailingDirectorySeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        internal static string EnsurePathIsWithinRoot(string rootPath, string candidatePath, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new ArgumentException("A root path is required.", nameof(rootPath));
            }

            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                throw new ArgumentException("A candidate path is required.", parameterName);
            }

            string rootFullPath = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidateFullPath = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (candidateFullPath.Equals(rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                return candidateFullPath;
            }

            string rootWithSeparator = rootFullPath + Path.DirectorySeparatorChar;
            if (!candidateFullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Resolved {parameterName} escapes the configured download directory.");
            }

            return candidateFullPath;
        }

        internal static string ResolveExecutablePath(string executablePathOrName)
        {
            if (string.IsNullOrWhiteSpace(executablePathOrName))
            {
                throw new ArgumentException("An executable path or file name is required.", nameof(executablePathOrName));
            }

            string trimmedExecutable = executablePathOrName.Trim().Trim('"');
            var candidates = new List<string>();

            if (Path.IsPathRooted(trimmedExecutable))
            {
                string rootedFullPath = Path.GetFullPath(trimmedExecutable);
                if (File.Exists(rootedFullPath))
                {
                    return rootedFullPath;
                }

                throw new FileNotFoundException($"Executable not found: {rootedFullPath}", rootedFullPath);
            }

            string[] namesToSearch = Path.HasExtension(trimmedExecutable)
                ? new[] { trimmedExecutable }
                : new[] { trimmedExecutable, trimmedExecutable + ".exe" };

            foreach (string fileName in namesToSearch)
            {
                candidates.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName));

                string[] pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                    .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string entry in pathEntries)
                {
                    string directory = entry.Trim().Trim('"');
                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }

                    try
                    {
                        candidates.Add(Path.Combine(directory, fileName));
                    }
                    catch
                    {
                        // Ignore malformed PATH entries.
                    }
                }
            }

            foreach (string candidate in candidates
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"Executable '{trimmedExecutable}' was not found in the application directory or PATH.", trimmedExecutable);
        }

        internal static bool TryParseInt(string value, out int result)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        internal static bool TryParseDouble(string value, out double result)
        {
            return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out result);
        }
    }
}
