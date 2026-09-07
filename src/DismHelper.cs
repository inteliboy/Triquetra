using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Triquetra
{
    static class DismHelper
    {
        // Matches DISM progress bars like [===57.6%===] or [===57,6%===]
        // DISM uses the OS locale decimal separator, so accept both . and ,
        static readonly Regex _progressRe =
            new Regex(@"\[=+\s*(\d[\d.,]*%)\s*=*\]", RegexOptions.Compiled);

        // Dispatches to the native DISM API (DismNative.cs) when enabled,
        // falling back to the dism.exe CLI path below on ANY native
        // failure - this is what makes it safe to leave the native path
        // on even though whether it truly accepts .msu/.esd for an ONLINE
        // session is unverified (see DismNative.cs's own header comment).
        public static int AddPackage(string packagePath, Action<string> progressCb)
        {
            if (AppConfig.UseDismApi)
            {
                int nativeRc;
                if (TryAddPackageNative(packagePath, progressCb, out nativeRc))
                    return nativeRc;
            }
            return AddPackageCli(packagePath, progressCb);
        }

        static bool TryAddPackageNative(string packagePath, Action<string> progressCb, out int rc)
        {
            string pkgName = Path.GetFileName(packagePath);
            rc = -1;
            try
            {
                Logger.Log("Using DISM API for " + pkgName + " ...");
                bool[] stop = new bool[] { false };
                StartHeartbeat(progressCb, stop);
                try { rc = DismNative.AddPackage(packagePath, progressCb); }
                finally { stop[0] = true; }
                if (DismRc.IsSuccess(rc))
                {
                    Logger.Log(string.Format("DISM API install finished: {0} (rc={1})", pkgName, rc));
                    return true;
                }
                string msg = DismNative.GetLastErrorMessage();
                Logger.Log(string.Format(
                    "DISM API rejected {0} (rc=0x{1:X8}): {2} - falling back to dism.exe.",
                    pkgName, rc, msg ?? "(no error message)"));
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log("DISM API call failed for " + pkgName + ": " + ex.Message +
                    " - falling back to dism.exe.");
                return false;
            }
        }

        // The native DismApi.dll progress callback goes just as silent as
        // dism.exe's own stdout percentage does for long stretches of a
        // large ESD/servicing-stack install - same underlying CBS engine
        // either way. AddPackageCli already worked around this with a
        // heartbeat (see its own comment); TryAddPackageNative/
        // TryResetBaseNative had no equivalent at all, so the status bar
        // showed literally nothing for the whole install when the native
        // path was on - confirmed by the user hitting exactly this live.
        // Any real progress DISM does send through still overwrites this
        // in between ticks (same "two sources call progressCb" pattern
        // AddPackageCli already relies on - each call independently
        // marshals to the UI thread, no new concurrency risk).
        static void StartHeartbeat(Action<string> progressCb, bool[] stop)
        {
            Thread t = new Thread(delegate()
            {
                int secs = 0;
                while (!stop[0])
                {
                    Thread.Sleep(1000);
                    secs++;
                    if (!stop[0] && progressCb != null)
                        progressCb("working ... " + secs + "s");
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        static int AddPackageCli(string packagePath, Action<string> progressCb)
        {
            string pkgName = Path.GetFileName(packagePath);
            Logger.Log("Installing " + pkgName + " ...");
            ProcessStartInfo psi = new ProcessStartInfo(
                "dism",
                "/Online /Add-Package /PackagePath:\"" + packagePath + "\" /NoRestart");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            psi.UseShellExecute        = false;
            psi.CreateNoWindow         = true;

            using (Process proc = Process.Start(psi))
            {
                // Drain stderr asynchronously so its pipe buffer never fills
                // and deadlocks DISM while we are blocked reading stdout.
                proc.ErrorDataReceived += delegate(object ds, DataReceivedEventArgs de) { };
                proc.BeginErrorReadLine();

                // ESD packages: DISM emits nothing for many minutes while
                // expanding payload internally. Run a heartbeat so the
                // status bar keeps ticking and the user knows we are alive.
                bool[] stop = new bool[] { false };
                StartHeartbeat(progressCb, stop);

                ReadStreamWithProgress(proc.StandardOutput.BaseStream, progressCb, null);

                stop[0] = true;
                proc.WaitForExit();
                int exitCode = proc.ExitCode;
                if (DismRc.NeedsReboot(exitCode))
                    Logger.Log(string.Format(
                        "Install finished: {0} (rc={1} – reboot required)", pkgName, exitCode));
                else
                    Logger.Log(string.Format(
                        "Install finished: {0} (rc={1})", pkgName, exitCode));
                return exitCode;
            }
        }

        public static int ResetBase(Action<string> lineCb, Action<string> progressCb)
        {
            if (AppConfig.UseDismApi)
            {
                int nativeRc;
                if (TryResetBaseNative(progressCb, out nativeRc))
                    return nativeRc;
            }
            return ResetBaseCli(lineCb, progressCb);
        }

        static bool TryResetBaseNative(Action<string> progressCb, out int rc)
        {
            rc = -1;
            try
            {
                Logger.Log("Using DISM API for component cleanup (ResetBase) ...");
                bool[] stop = new bool[] { false };
                StartHeartbeat(progressCb, stop);
                try { rc = DismNative.ResetBase(progressCb); }
                finally { stop[0] = true; }
                if (DismRc.IsSuccess(rc))
                {
                    Logger.Log("DISM API ResetBase finished (rc=" + rc + ")");
                    return true;
                }
                string msg = DismNative.GetLastErrorMessage();
                Logger.Log(string.Format(
                    "DISM API ResetBase failed (rc=0x{0:X8}): {1} - falling back to dism.exe.",
                    rc, msg ?? "(no error message)"));
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log("DISM API ResetBase call failed: " + ex.Message +
                    " - falling back to dism.exe.");
                return false;
            }
        }

        static int ResetBaseCli(Action<string> lineCb, Action<string> progressCb)
        {
            Logger.Log("Starting DISM /StartComponentCleanup /ResetBase ...");
            string psCmd =
                "$p = Start-Process -FilePath dism " +
                "-ArgumentList '/Online','/Cleanup-Image'," +
                "'/StartComponentCleanup','/ResetBase' " +
                "-Wait -PassThru -NoNewWindow; exit $p.ExitCode";
            ProcessStartInfo psi = new ProcessStartInfo(
                "powershell",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + psCmd + "\"");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            psi.UseShellExecute        = false;
            psi.CreateNoWindow         = true;
            using (Process proc = Process.Start(psi))
            {
                ReadStreamWithProgress(proc.StandardOutput.BaseStream, progressCb, lineCb);
                proc.WaitForExit();
                return proc.ExitCode;
            }
        }

        // Nudges the servicing stack to re-scan installed packages between
        // the SSU and ESD install steps (registers the new SSU before the
        // ESD needs it) - the returned package list/count is discarded
        // either way, this call is used only for its side effect.
        public static void RefreshPackageState(Action<string> statusCb)
        {
            if (AppConfig.UseDismApi)
            {
                try
                {
                    Logger.Log("Using DISM API to refresh package state ...");
                    int count = DismNative.GetPackageCount();
                    if (count >= 0)
                    {
                        Logger.Log("DISM API package state refreshed (" + count + " packages).");
                        return;
                    }
                    Logger.Log("DISM API package refresh returned no result - falling back to dism.exe.");
                }
                catch (Exception ex)
                {
                    Logger.Log("DISM API package refresh failed: " + ex.Message + " - falling back to dism.exe.");
                }
            }
            RefreshPackageStateCli(statusCb);
        }

        static void RefreshPackageStateCli(Action<string> statusCb)
        {
            Logger.Log("Running dism /Get-Packages to refresh state ...");
            ProcessStartInfo psi = new ProcessStartInfo("dism", "/Online /Get-Packages");
            psi.CreateNoWindow         = true;
            psi.UseShellExecute        = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError  = true;
            using (Process proc = Process.Start(psi))
            {
                // Must drain stdout/stderr async to prevent the pipe-buffer
                // deadlock (DISM blocks writing output, we block on
                // WaitForExit - classic deadlock).
                proc.OutputDataReceived += delegate(object ds, DataReceivedEventArgs de) { };
                proc.ErrorDataReceived  += delegate(object ds, DataReceivedEventArgs de) { };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                // Show a heartbeat while waiting (can take 1-3 min)
                bool[] done = new bool[] { false };
                Thread beat = new Thread(delegate()
                {
                    int s = 0;
                    while (!done[0])
                    {
                        Thread.Sleep(1000);
                        s++;
                        if (!done[0] && statusCb != null)
                            statusCb("Refreshing DISM package state ... " + s + "s");
                    }
                });
                beat.IsBackground = true;
                beat.Start();
                proc.WaitForExit();
                done[0] = true;
            }
            Logger.Log("Package state refreshed.");
        }

        static void ReadStreamWithProgress(Stream stream,
            Action<string> progressCb, Action<string> lineCb)
        {
            List<byte> buf = new List<byte>(256);
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == '\r' || b == '\n')
                {
                    if (buf.Count > 0)
                    {
                        string line = Encoding.UTF8.GetString(buf.ToArray()).Trim();
                        buf.Clear();
                        if (string.IsNullOrEmpty(line)) continue;
                        Match m = _progressRe.Match(line);
                        if (m.Success) { if (progressCb != null) progressCb(m.Groups[1].Value); }
                        else { Logger.Log(line); if (lineCb != null) lineCb(line); }
                    }
                }
                else buf.Add((byte)b);
            }
            if (buf.Count > 0)
            {
                string line = Encoding.UTF8.GetString(buf.ToArray()).Trim();
                if (!string.IsNullOrEmpty(line) && !_progressRe.IsMatch(line))
                { Logger.Log(line); if (lineCb != null) lineCb(line); }
            }
        }
    }
}
