using System;
using System.IO;
using System.Text;

namespace Triquetra
{
    static class Logger
    {
        static readonly string _logFile =
            Path.Combine(AppConfig.ProgramDataDir, "triquetra.log");

        // Fired on the calling thread; subscribers must marshal to UI thread.
        public static event Action<string> MessageLogged;

        public static string LogFilePath { get { return _logFile; } }

        public static void Log(string msg)
        {
            string entry = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC") + "\t" + msg;
            try { File.AppendAllText(_logFile, entry + "\r\n", Encoding.UTF8); }
            catch { /* best effort */ }
            Action<string> h = MessageLogged;
            if (h != null) h(msg);
        }

        public static void ClearFile()
        {
            File.WriteAllText(_logFile, "", Encoding.UTF8);
        }
    }
}
