using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Triquetra
{
    static class OsCheck
    {
        public static void CheckNotServerOs()
        {
            try
            {
                RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null) return;
                string edition  = (key.GetValue("EditionID")         ?? "").ToString();
                string instType = (key.GetValue("InstallationType")   ?? "").ToString();
                string product  = (key.GetValue("ProductName")        ?? "Windows").ToString();
                key.Close();
                Logger.Log(string.Format("OS: {0} | SKU: {1} | Type: {2}",
                    product, edition, instType));
                if (!instType.Equals("Client", StringComparison.OrdinalIgnoreCase))
                {
                    ThemedMessageBox.Show(null,
                        string.Format("Not supported on {0} edition.", instType),
                        "Unsupported OS", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Environment.Exit(1);
                }
            }
            catch (Exception ex) { Logger.Log("Failed to check OS type: " + ex.Message); }
        }
    }
}
