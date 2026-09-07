using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Triquetra
{
    static class SelfUpdater
    {
        // Returns true only if an update was downloaded and applied (process exits).
        // Returns false on any error or when already up-to-date, never throws.
        public static bool TryUpdate(string remoteUrl)
        {
            try
            {
                string selfPath = Process.GetCurrentProcess().MainModule.FileName;
                if (!selfPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log("Not a compiled EXE – skipping self-update.");
                    return false;
                }

                Net.RemoteHash remoteHash;
                try { remoteHash = Net.FetchRemoteHash(remoteUrl); }
                catch (Exception ex)
                {
                    Logger.Log("Could not fetch remote checksum: " + ex.Message);
                    return false;   // ← do NOT exit; just skip update
                }

                string localHash;
                try { localHash = Net.FileHash(selfPath, remoteHash.Algorithm); }
                catch { localHash = null; }

                Logger.Log("Local EXE:  " + (localHash ?? "(unreadable)") +
                    " (" + remoteHash.Algorithm + ")");
                Logger.Log("Remote EXE: " + remoteHash.Hash +
                    " (" + remoteHash.Algorithm + ")");

                if (localHash != null &&
                    localHash.Equals(remoteHash.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log("Triquetra is up-to-date.");
                    return false;
                }

                Logger.Log("New version found – downloading ...");
                string stagingPath =
                    Path.Combine(AppConfig.ProgramDataDir, "triquetra_update.exe");
                try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }

                // Download staging copy
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(remoteUrl);
                    req.Credentials     = new NetworkCredential(AppConfig.User, AppConfig.Password);
                    req.PreAuthenticate = true;
                    req.Timeout         = 60000;
                    req.ServerCertificateValidationCallback =
                        delegate(object s,
                            System.Security.Cryptography.X509Certificates.X509Certificate c,
                            System.Security.Cryptography.X509Certificates.X509Chain ch,
                            System.Net.Security.SslPolicyErrors e) { return true; };
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (FileStream dst = File.Create(stagingPath))
                    {
                        byte[] buf = new byte[1024 * 1024];
                        int r;
                        Stream src = resp.GetResponseStream();
                        while ((r = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, r);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("Self-update download failed: " + ex.Message);
                    return false;
                }

                // Verify staging checksum
                try
                {
                    string stagingHash = Net.FileHash(stagingPath, remoteHash.Algorithm);
                    if (!stagingHash.Equals(remoteHash.Hash, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log("Staging " + remoteHash.Algorithm +
                            " mismatch – aborting update.");
                        try { File.Delete(stagingPath); } catch { }
                        return false;
                    }
                    Logger.Log("Staging " + remoteHash.Algorithm + " verified: " + stagingHash);
                }
                catch (Exception ex) { Logger.Log("Checksum verify failed: " + ex.Message); return false; }

                // Write PowerShell swap script
                int    pid        = Process.GetCurrentProcess().Id;
                string scriptPath = Path.Combine(AppConfig.ProgramDataDir, "triquetra_update.ps1");
                string psScript   =
                    "$pid_to_watch = " + pid + "\r\n" +
                    "$source       = '" + stagingPath + "'\r\n" +
                    "$destination  = '" + selfPath    + "'\r\n" +
                    "while (Get-Process -Id $pid_to_watch -ErrorAction SilentlyContinue) {\r\n" +
                    "    Start-Sleep -Milliseconds 200\r\n}\r\n" +
                    "Start-Sleep -Milliseconds 800\r\n" +
                    "try {\r\n" +
                    "    Copy-Item -Path $source -Destination $destination -Force\r\n" +
                    "    Remove-Item -Path $source -Force -ErrorAction SilentlyContinue\r\n" +
                    "} catch { exit 1 }\r\n" +
                    "Start-Process -FilePath $destination\r\n" +
                    "Remove-Item -Path '" + scriptPath + "' -Force -ErrorAction SilentlyContinue\r\n";

                try { File.WriteAllText(scriptPath, psScript, Encoding.UTF8); }
                catch (Exception ex) { Logger.Log("Failed to write updater script: " + ex.Message); return false; }

                ProcessStartInfo psi = new ProcessStartInfo(
                    "powershell",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + scriptPath + "\"");
                psi.CreateNoWindow  = true;
                psi.UseShellExecute = false;
                Process.Start(psi);

                Logger.Log("Restarting with updated binary ...");
                Environment.Exit(0);
                return true; // unreachable
            }
            catch (Exception ex)
            {
                Logger.Log("Self-update error: " + ex.Message);
                return false;
            }
        }
    }
}
