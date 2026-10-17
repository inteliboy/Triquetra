using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Triquetra
{
    // Remembers "build X was installed at time T" across the reboot that
    // finishes a servicing install, so the next start can tell whether the
    // build actually changed.
    //
    // Why this exists: for MUMX v2 updates (KB5127936 / 26100.9807 onward,
    // see UUPDUMPFetcher's CLAUDE.md) dism /Add-Package can report success
    // while the boot-time servicing transaction later reverts the
    // components and the OS stays on the old build. dism's exit code alone
    // therefore cannot confirm the update; only the build number after the
    // reboot can.
    static class PendingInstall
    {
        [DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        static string FilePath
        {
            get { return Path.Combine(AppConfig.ProgramDataDir, "pending_install.txt"); }
        }

        public static void Record(string targetBuild)
        {
            try
            {
                File.WriteAllText(FilePath,
                    "target=" + targetBuild + "\r\n" +
                    "time=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "\r\n");
            }
            catch (Exception ex) { Logger.Log("Could not record pending install: " + ex.Message); }
        }

        static void Clear()
        {
            try { File.Delete(FilePath); } catch { }
        }

        static DateTime BootTimeUtc()
        {
            return DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64());
        }

        // Compares the recorded target with the running build. Returns a
        // warning to show the user when the machine has rebooted since the
        // install but is still below the target; otherwise null (nothing
        // recorded, confirmed, or reboot still pending).
        public static string Check(int[] localParts)
        {
            if (!File.Exists(FilePath) || localParts == null || localParts.Length == 0)
                return null;

            string target = null;
            long ticks = 0;
            try
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    if (k == "target") target = v;
                    else if (k == "time") long.TryParse(v, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out ticks);
                }
                if (target == null || ticks == 0) { Clear(); return null; }

                int[] targetParts = VersionCompare.Parse(target);
                if (VersionCompare.Compare(localParts, targetParts) >= 0)
                {
                    Logger.Log("Previous install of " + target + " is confirmed (running build is at or above it).");
                    Clear();
                    return null;
                }

                DateTime installed = new DateTime(ticks, DateTimeKind.Utc);
                if (BootTimeUtc() > installed)
                {
                    Clear();
                    string msg = "The last install (" + target + ", installed " +
                        installed.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
                        ") did not take effect: the machine has rebooted since, but is still on " +
                        string.Join(".", Array.ConvertAll(localParts, delegate(int p) { return p.ToString(); })) +
                        ".\n\nThe servicing transaction was probably rolled back during boot. " +
                        "Check C:\\Windows\\Logs\\CBS\\CBS.log, then try the install again.";
                    Logger.Log("WARNING: " + msg.Replace("\n\n", " ").Replace("\n", " "));
                    return msg;
                }

                Logger.Log("Install of " + target + " is waiting for a reboot to finish.");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log("Pending-install check failed: " + ex.Message);
                return null;
            }
        }
    }
}
