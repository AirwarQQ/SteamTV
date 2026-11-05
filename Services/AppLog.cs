// The log in the window is lost on restart; this keeps a copy on disk for bug reports:
// %LOCALAPPDATA%\NekoCouch\nekocouch.log, rotated to nekocouch.old.log at 1 MB.
using System;
using System.IO;

namespace NekoCouch
{
    internal static class AppLog
    {
        public static readonly string FilePath = Path.Combine(AdbBundle.Dir, "nekocouch.log");
        private const long MaxBytes = 1024 * 1024;
        private static readonly object Gate = new object();

        public static void Write(string line)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(AdbBundle.Dir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        string old = Path.Combine(AdbBundle.Dir, "nekocouch.old.log");
                        File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
                }
                catch { }   // logging must never break the app
            }
        }
    }
}
