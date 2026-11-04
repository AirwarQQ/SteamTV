// Self-update: downloads the release zip, swaps SteamTV.exe in place and restarts. Windows lets a
// running exe be renamed (not overwritten), so the old one moves aside to SteamTV.old.exe and is
// deleted on the next start.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace SteamTV
{
    internal static class Updater
    {
        public const string WaitArg = "--after-update";   // new exe: wait for the old one to exit

        private static string ExePath => Assembly.GetEntryAssembly().Location;
        private static string OldExePath => Path.ChangeExtension(ExePath, ".old.exe");

        public static void CleanUpOldExe()
        {
            try { if (File.Exists(OldExePath)) File.Delete(OldExePath); } catch { }
        }

        // Downloads and installs `tag` (e.g. "v1.13.0"), then starts the new exe. Returns null on
        // success — the caller then exits — or why it failed (nothing was changed in that case).
        public static async Task<string> InstallAsync(string tag, bool startHidden)
        {
            string temp = Path.Combine(Path.GetTempPath(), "SteamTV-update-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                string zip = Path.Combine(temp, "update.zip");
                string url = "https://github.com/AirwarQQ/SteamTV/releases/download/" + tag + "/SteamTV-" + tag + "-win.zip";
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("SteamTV-Updater");
                    using (var response = await client.GetAsync(url).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return "download failed (" + (int)response.StatusCode + ")";
                        using (var file = File.Create(zip)) await response.Content.CopyToAsync(file).ConfigureAwait(false);
                    }
                }

                string newExe = Path.Combine(temp, "SteamTV.exe");
                using (var archive = ZipFile.OpenRead(zip))
                {
                    var entry = archive.Entries.FirstOrDefault(x => string.Equals(x.Name, "SteamTV.exe", StringComparison.OrdinalIgnoreCase));
                    if (entry == null) return "the release archive has no SteamTV.exe";
                    entry.ExtractToFile(newExe, true);
                }

                // Only ever move forward, and only to something that really is a SteamTV build.
                var info = FileVersionInfo.GetVersionInfo(newExe);
                Version got;
                if (info.ProductName != "SteamTV" || !Version.TryParse(info.FileVersion, out got) ||
                    got <= Assembly.GetExecutingAssembly().GetName().Version)
                    return "the downloaded file isn't a newer SteamTV (" + info.ProductName + " " + info.FileVersion + ")";

                CleanUpOldExe();
                File.Move(ExePath, OldExePath);
                try { File.Copy(newExe, ExePath); }
                catch { File.Move(OldExePath, ExePath); throw; }   // put the running version back

                Process.Start(new ProcessStartInfo(ExePath, WaitArg + (startHidden ? " -autostart" : "")) { UseShellExecute = false });
                return null;
            }
            catch (UnauthorizedAccessException) { return "no permission to replace " + ExePath + " — download it from the releases page instead"; }
            catch (Exception ex) { return ex.Message; }
            finally { try { Directory.Delete(temp, true); } catch { } }
        }
    }
}
