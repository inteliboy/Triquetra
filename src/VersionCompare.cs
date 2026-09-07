using System;

namespace Triquetra
{
    static class VersionCompare
    {
        public static int Compare(int[] a, int[] b)
        {
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int ai = i < a.Length ? a[i] : 0;
                int bi = i < b.Length ? b[i] : 0;
                if (ai != bi) return ai > bi ? 1 : -1;
            }
            return 0;
        }

        public static int[] Parse(string ver)
        {
            return Array.ConvertAll(ver.Split('.'), int.Parse);
        }
    }

    // 0     = success
    // 3010  = success, reboot required (ERROR_SUCCESS_REBOOT_REQUIRED)
    // 3011  = success, reboot initiated
    static class DismRc
    {
        public static bool IsSuccess(int rc)
        {
            return rc == 0 || rc == 3010 || rc == 3011;
        }

        public static bool NeedsReboot(int rc)
        {
            return rc == 3010 || rc == 3011;
        }
    }
}
