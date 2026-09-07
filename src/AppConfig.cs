using System;
using System.IO;
using System.Text;

namespace Triquetra
{
    static class AppConfig
    {
        public static readonly string ProgramDataDir;
        static readonly string _confFile;

        public static string ConfFilePath { get { return _confFile; } }

        // No default mirror/credentials are baked in - must be configured
        // in the Settings tab before Refresh/Install/self-update can do
        // anything.
        //
        // BaseUrl/MirrorUrl normalize to always end in exactly one "/" (or
        // stay "" if unset) right here, on assignment - so every consumer
        // (Load(), the Settings tab, a future call site) can just
        // concatenate a relative path directly, never needing its own
        // TrimEnd('/')+"/" dance. Before this, that pattern was repeated at
        // half a dozen call sites and still needed the user to type a
        // trailing slash themselves for it to look "normal" - now typing
        // "https://updates.smce.pl" (no slash) works identically to typing
        // it with one.
        static string _baseUrl = "";
        static string _mirrorUrl = "";
        public static string BaseUrl
        {
            get { return _baseUrl; }
            set { _baseUrl = NormalizeUrl(value); }
        }
        public static string MirrorUrl
        {
            get { return _mirrorUrl; }
            set { _mirrorUrl = NormalizeUrl(value); }
        }
        static string NormalizeUrl(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            v = v.Trim();
            return v.Length == 0 ? "" : v.TrimEnd('/') + "/";
        }

        public static string User     = "";
        public static string Password = "";
        public static bool   DisableSelfUpdate = false;
        public static bool   DryRun   = false;
        public static bool   Yes      = false;

        // Experimental: use DismNative (P/Invoke DismApi.dll) instead of
        // shelling out to dism.exe/powershell.exe. DismHelper falls back to
        // the CLI path automatically on any native failure, so this is safe
        // to leave on - default off since .msu/.esd acceptance for an
        // ONLINE session is unverified (see DismNative.cs).
        public static bool   UseDismApi = false;

        // "system" (follow the OS setting), "dark", or "light" - set by the
        // in-app theme toggle so a manual override survives a restart.
        public static string Theme = "system";

        // Opt-in hedged multi-mirror downloader (AcceleratedDownload.cs),
        // modeled on our DeploymentTool project's updater. Disabled by
        // default; MirrorUrl (declared above, alongside BaseUrl)/
        // HedgeBudgetPercent are only meaningful (and only shown enabled in
        // the Settings tab) once this is on.
        public static bool   AccelDownloads     = false;
        public static int    HedgeBudgetPercent = 10; // matches DeploymentTool's seeded default

        static AppConfig()
        {
            ProgramDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "triquetra");
            _confFile = Path.Combine(ProgramDataDir, "triquetra.conf");
            try { Directory.CreateDirectory(ProgramDataDir); } catch { }
        }

        public static void Load()
        {
            if (!File.Exists(_confFile)) return;
            try
            {
                string[] lines = File.ReadAllLines(_confFile, Encoding.UTF8);
                bool inSection = false;
                foreach (string line in lines)
                {
                    string l = line.Trim();
                    if (l.StartsWith("["))
                    { inSection = l == "[triquetra]"; continue; }
                    if (!inSection || !l.Contains("=")) continue;
                    int    idx = l.IndexOf('=');
                    string k   = l.Substring(0, idx).Trim().ToLower();
                    string v   = l.Substring(idx + 1).Trim();
                    switch (k)
                    {
                        case "base_url":  BaseUrl  = v; break;
                        case "user":      User     = v; break;
                        case "password":  Password = v; break;
                        case "disable_self_update": DisableSelfUpdate = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "dry_run":   DryRun   = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "yes":       Yes      = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "use_dism_api": UseDismApi = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "accel_downloads": AccelDownloads = v.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "mirror_url": MirrorUrl = v; break;
                        case "hedge_budget_percent":
                        {
                            int pct;
                            HedgeBudgetPercent = int.TryParse(v, out pct) ? Math.Max(0, Math.Min(100, pct)) : 10;
                            break;
                        }
                        case "theme":
                            if (v.Equals("dark", StringComparison.OrdinalIgnoreCase) ||
                                v.Equals("light", StringComparison.OrdinalIgnoreCase))
                                Theme = v.ToLower();
                            else
                                Theme = "system";
                            break;
                    }
                }
            }
            catch (Exception ex) { Logger.Log("Failed to read config: " + ex.Message); }
        }

        public static void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[triquetra]");
            sb.AppendLine("base_url = " + BaseUrl);
            sb.AppendLine("user = " + User);
            sb.AppendLine("password = " + Password);
            sb.AppendLine("disable_self_update = " + DisableSelfUpdate.ToString().ToLower());
            sb.AppendLine("dry_run = " + DryRun.ToString().ToLower());
            sb.AppendLine("yes = " + Yes.ToString().ToLower());
            sb.AppendLine("use_dism_api = " + UseDismApi.ToString().ToLower());
            sb.AppendLine("accel_downloads = " + AccelDownloads.ToString().ToLower());
            sb.AppendLine("mirror_url = " + MirrorUrl);
            sb.AppendLine("hedge_budget_percent = " + HedgeBudgetPercent);
            sb.AppendLine("theme = " + Theme);
            try { File.WriteAllText(_confFile, sb.ToString(), Encoding.UTF8); }
            catch (Exception ex) { Logger.Log("Failed to save config: " + ex.Message); }
        }
    }
}
