using System;
using System.Collections.Generic;

namespace Triquetra
{
    static class EnablementPackage
    {
        // Paths are relative to AppConfig.BaseUrl (resolved at use time, not
        // baked in here) - the EP cab files live in the same configured
        // mirror as everything else, under EP/<arch>/<file>.
        static readonly Dictionary<string, Dictionary<string, string>> _paths;

        static EnablementPackage()
        {
            _paths = new Dictionary<string, Dictionary<string, string>>();
            Dictionary<string, string> ep22 = new Dictionary<string, string>();
            ep22["amd64"] = "EP/amd64/Windows11.0-KB5027397-x64.cab";
            ep22["arm64"] = "EP/arm64/Windows11.0-KB5027397-arm64.cab";
            _paths["22621"] = ep22;
            Dictionary<string, string> ep26 = new Dictionary<string, string>();
            ep26["amd64"] = "EP/amd64/Windows11.0-KB5121794-x64.cab";
            ep26["arm64"] = "EP/arm64/Windows11.0-KB5121794-arm64.cab";
            _paths["26100"] = ep26;
        }

        public static void CheckAndOffer(int localMajor, int[] localParts,
            string arch, bool dryRun,
            Func<string, bool> askFn,
            Action<string> alreadyOnFn)
        {
            string dispVer = RegistryHelper.GetDisplayVersion();
            string epBranch, epPrompt;
            int    epMin;

            if      (localMajor == 22621) { epBranch = "22621"; epPrompt = "23H2"; epMin = 2506; }
            else if (localMajor == 26100) { epBranch = "26100"; epPrompt = "26H2"; epMin = 8966; }
            else { Logger.Log("No EP defined for build " + localMajor); return; }

            if (dispVer == null) { Logger.Log("Cannot read DisplayVersion."); return; }
            if (dispVer == epPrompt)
            {
                Logger.Log("Already on " + epPrompt + " – EP not needed.");
                if (alreadyOnFn != null) alreadyOnFn(epPrompt);
                return;
            }
            if (localParts.Length < 2 || localParts[1] < epMin)
            {
                Logger.Log(string.Format("Build too low for EP (need {0}.{1}).",
                    epBranch, epMin));
                return;
            }

            if (string.IsNullOrEmpty(AppConfig.BaseUrl))
            {
                Logger.Log("No base URL configured - cannot fetch Enablement Package.");
                return;
            }

            if (askFn("Install " + epPrompt + " Enablement Package?"))
            {
                string epUrl  = AppConfig.BaseUrl + _paths[epBranch][arch];
                string epPath = Net.DownloadFile(epUrl, AppConfig.ProgramDataDir);
                if (!dryRun)
                    DismHelper.AddPackage(epPath,
                        delegate(string pct) { Logger.Log("EP install progress: " + pct); });
            }
            else Logger.Log("EP installation declined.");
        }
    }
}
