using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SteamTV
{
    internal static class HdmiInstaller
    {
        public static string Install(Adb adb, Action<string> report)
        {
            if (!adb.IsAdbReachable())
                throw new InvalidOperationException("Cannot connect to the TV. Check its IP and accept the USB debugging prompt on the TV.");
            var failures = new List<string>();
            for (int port = 1; port <= 3; port++)
            {
                string name = "HDMI" + port;
                string package = "com.liskovsoft.tvexternalsource.hdmi" + port;
                string temp = null;
                try
                {
                    if (adb.IsPackageInstalled(package))
                    { report(name + " is already installed — skipped."); continue; }
                    report("Installing " + name + "…");
                    temp = Path.Combine(Path.GetTempPath(), "SteamTV-" + Guid.NewGuid().ToString("N") + ".apk");
                    using (var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamTV.hdmi" + port + ".apk"))
                    {
                        if (source == null) throw new InvalidOperationException("Bundled APK is missing.");
                        using (var file = File.Create(temp)) source.CopyTo(file);
                    }
                    adb.InstallApk(temp);
                    if (!adb.IsPackageInstalled(package)) throw new InvalidOperationException("Package was not found after installation.");
                    report(name + " installed.");
                }
                catch (Exception ex)
                {
                    failures.Add(name);
                    report(name + ": " + ex.Message);
                }
                finally
                {
                    if (temp != null)
                    {
                        try { File.Delete(temp); }
                        catch (Exception ex) { report("Could not remove temporary APK: " + ex.Message); }
                    }
                }
            }
            return failures.Count == 0 ? "HDMI1, HDMI2 and HDMI3 are installed. Select the PC's HDMI port above."
                : "Installation failed for " + string.Join(", ", failures) + ". See the log; retry after fixing the error.";
        }
    }
}
