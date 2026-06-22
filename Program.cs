// SteamTV — entry point.
// See README.md for project description, build options, and settings location.
using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace SteamTV
{

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool autostart = args.Any(a =>
                a.Equals("-autostart", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/autostart", StringComparison.OrdinalIgnoreCase));

            // Prevent a second instance (manual start + autostart must not duplicate)
            bool createdNew;
            using (var mutex = new Mutex(true, "SteamTV_SingleInstance_8f2b1c", out createdNew))
            {
                if (!createdNew) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(autostart));
            }
        }
    }

}