using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Triquetra
{
    static class RegistryHelper
    {
        public static string GetArch()
        {
            try
            {
                RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null) return "amd64";
                string ble = (key.GetValue("BuildLabEx") ?? "").ToString();
                key.Close();
                return ble.IndexOf("arm64", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "arm64" : "amd64";
            }
            catch (Exception ex)
            { Logger.Log("Failed to read BuildLabEx: " + ex.Message); return "amd64"; }
        }

        public static string GetDisplayVersion()
        {
            try
            {
                RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null) return null;
                object v = key.GetValue("DisplayVersion");
                key.Close();
                return v != null ? v.ToString() : null;
            }
            catch { return null; }
        }

        public static string GetLocalBuildVersion()
        {
            try
            {
                RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null) return null;
                string buildLab = (key.GetValue("BuildLab") ?? "").ToString();
                object ubr      = key.GetValue("UBR");
                key.Close();
                Match m = Regex.Match(buildLab, @"^(\d+)\.");
                if (!m.Success)
                { Logger.Log("Unexpected BuildLab: " + buildLab); return null; }
                return m.Groups[1].Value + "." + (ubr ?? "0");
            }
            catch (Exception ex)
            { Logger.Log("Failed to read BuildLab/UBR: " + ex.Message); return null; }
        }

        public static VersionInfo NormalizeVersion(string full)
        {
            MatchCollection mc   = Regex.Matches(full, @"\d+");
            List<int>       nums = new List<int>();
            foreach (Match m in mc) nums.Add(int.Parse(m.Value));

            VersionInfo vi = new VersionInfo();
            if (nums.Count == 0) { vi.Short = full; vi.Parts = new int[0]; return vi; }
            int[] parts = nums.Count >= 2
                ? new int[] { nums[0], nums[1] }
                : new int[] { nums[0] };
            vi.Short = string.Join(".", Array.ConvertAll(parts, x => x.ToString()));
            vi.Parts = parts;
            return vi;
        }

        // Real OS setting - used as the startup default only when
        // AppConfig.Theme is "system" (no explicit user override yet).
        public static bool IsDarkMode()
        {
            try
            {
                RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key == null) return false;
                object val = key.GetValue("AppsUseLightTheme");
                key.Close();
                return (val is int) && ((int)val == 0);
            }
            catch { return false; }
        }
    }
}
