using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Triquetra
{
    static class Net
    {
        public static string FetchText(string url)
        {
            HttpWebRequest req = MakeRequest(url, 30000);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static bool RemoteFileExists(string url)
        {
            try
            {
                HttpWebRequest req = MakeRequest(url, 10000);
                req.Method = "HEAD";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        public static string FetchMd5(string url)
        {
            string raw = FetchText(url).Trim();
            return raw.Split(new char[] { ' ', '\t' })[0].ToLower();
        }

        public static string FileMd5(string path)
        {
            using (MD5 md5 = MD5.Create())
            using (FileStream fs = File.OpenRead(path))
                return BitConverter.ToString(md5.ComputeHash(fs))
                                   .Replace("-", "").ToLower();
        }

        public static string FetchSha256(string url)
        {
            string raw = FetchText(url).Trim();
            return raw.Split(new char[] { ' ', '\t' })[0].ToLower();
        }

        public static string FileSha256(string path)
        {
            // SHA256.Create() resolves to the CNG-backed implementation on
            // .NET 4.8 / Windows, which transparently uses the CPU's SHA
            // extensions (SHA-NI) when present – no manual CPUID check needed.
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs))
                                   .Replace("-", "").ToLower();
        }

        // Represents a remote checksum together with the algorithm it was
        // published under, so callers can hash the local file the same way.
        public sealed class RemoteHash
        {
            public string Algorithm; // "SHA256" or "MD5"
            public string Hash;
        }

        // Looks for "<url>.sha256" first (preferred – faster / hardware
        // accelerated where available) and falls back to "<url>.md5" if the
        // server doesn't publish a SHA-256 sidecar file.
        public static RemoteHash FetchRemoteHash(string url)
        {
            string sha256Url = url + ".sha256";
            if (RemoteFileExists(sha256Url))
            {
                try
                {
                    string h = FetchSha256(sha256Url);
                    return new RemoteHash { Algorithm = "SHA256", Hash = h };
                }
                catch (Exception ex)
                {
                    Logger.Log("Failed to fetch .sha256, falling back to .md5: " +
                        ex.Message);
                }
            }

            string md5Url = url + ".md5";
            string md5    = FetchMd5(md5Url);
            return new RemoteHash { Algorithm = "MD5", Hash = md5 };
        }

        public static string FileHash(string path, string algorithm)
        {
            return algorithm == "SHA256" ? FileSha256(path) : FileMd5(path);
        }

        // progressCb(bytesDone, totalBytes [-1 = unknown], speedMBs)
        public static string DownloadFile(string url, string destDir,
            Action<long, long, double> progressCb)
        {
            // Try the hedged multi-mirror path first when enabled - it
            // silently declines (returns false) for anything not eligible
            // (disabled, no mirror configured, file too small, mirrors
            // disagree, every restart attempt failed), so the plain single-
            // connection path below is always the fallback, not a
            // conditionally-compiled alternative. No caller of DownloadFile
            // needs to know or care which path actually ran.
            if (AppConfig.AccelDownloads)
            {
                string accelPath;
                if (AcceleratedDownload.TryDownload(url, destDir, progressCb, out accelPath))
                    return accelPath;
            }

            string fname    = Uri.UnescapeDataString(url.Split('/').Last());
            string destPath = Path.Combine(destDir, fname);
            int    maxTries = 3;

            while (true)
            {
                try
                {
                    if (File.Exists(destPath))
                    {
                        try
                        {
                            RemoteHash rh = FetchRemoteHash(url);
                            if (FileHash(destPath, rh.Algorithm)
                                    .Equals(rh.Hash, StringComparison.OrdinalIgnoreCase))
                            {
                                Logger.Log(fname + ": cached copy OK (" + rh.Algorithm +
                                    "), skipping download.");
                                return destPath;
                            }
                        }
                        catch { Logger.Log(fname + ": hash check failed, re-downloading."); }
                    }

                    Logger.Log("Downloading " + fname + " ...");
                    HttpWebRequest req = MakeRequest(url, 60000);
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    {
                        long total = resp.ContentLength; // -1 when unknown
                        using (Stream src = resp.GetResponseStream())
                        using (FileStream dst = File.Create(destPath))
                        {
                            byte[]   buf  = new byte[1024 * 1024];
                            long     done = 0;
                            DateTime t0   = DateTime.UtcNow;
                            int      read;
                            while ((read = src.Read(buf, 0, buf.Length)) > 0)
                            {
                                dst.Write(buf, 0, read);
                                done += read;
                                if (progressCb != null)
                                {
                                    double elapsed = (DateTime.UtcNow - t0).TotalSeconds + 1e-9;
                                    progressCb(done, total, done / elapsed / 1048576.0);
                                }
                            }
                        }
                    }
                    Logger.Log("Download complete: " + fname);
                    return destPath;
                }
                catch (Exception ex)
                {
                    Logger.Log(string.Format("Download failed ({0}): {1}", fname, ex.Message));
                    if (--maxTries <= 0)
                        throw new Exception("All retries exhausted for " + fname, ex);
                    Logger.Log(string.Format("Retrying {0} ({1} left) ...", fname, maxTries));
                    Thread.Sleep(3000);
                }
            }
        }

        public static string DownloadFile(string url, string destDir)
        {
            return DownloadFile(url, destDir, null);
        }

        static HttpWebRequest MakeRequest(string url, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Credentials     = new NetworkCredential(AppConfig.User, AppConfig.Password);
            req.PreAuthenticate = true;
            req.Timeout         = timeoutMs;
            req.ServerCertificateValidationCallback =
                delegate(object s,
                    System.Security.Cryptography.X509Certificates.X509Certificate c,
                    System.Security.Cryptography.X509Certificates.X509Chain ch,
                    System.Net.Security.SslPolicyErrors e) { return true; };
            req.UserAgent = "TriquetraUpdater/2.0.4";
            return req;
        }
    }
}
