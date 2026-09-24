// Checks GitHub for a newer release than the one currently running. No API calls that need
// auth/rate-limit handling — just follows the "latest release" redirect and reads its tag.
using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamTV
{
    internal static class UpdateChecker
    {
        private const string LatestReleaseUrl = "https://github.com/AirwarQQ/SteamTV/releases/latest";
        public const string ReleasesPageUrl = LatestReleaseUrl;

        // Returns the latest release tag (e.g. "v1.4.1"), or null if the check failed.
        public static async Task<string> GetLatestTagAsync()
        {
            try
            {
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamTV-UpdateCheck");
                    using (var response = await client.GetAsync(LatestReleaseUrl).ConfigureAwait(false))
                    {
                        var location = response.Headers.Location;
                        if (location == null) return null;
                        var match = Regex.Match(location.ToString(), @"/tag/(v[0-9.]+)$");
                        return match.Success ? match.Groups[1].Value : null;
                    }
                }
            }
            catch { return null; }
        }

        // True if latestTag (e.g. "v1.5.0") is newer than the currently running version.
        public static bool IsNewer(string latestTag, Version current)
        {
            if (string.IsNullOrEmpty(latestTag)) return false;
            Version latest;
            return Version.TryParse(latestTag.TrimStart('v', 'V'), out latest) && latest > current;
        }
    }
}
