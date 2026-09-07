// Triquetra.cs  -  Windows 11 Updater (WinForms .NET 4.8 edition)
//
// C# 5 compatible – no string interpolation, no value tuples, no local functions.
//
// Compile: see build.cmd in the project root.
//
// NOTE: An embedded application manifest requesting requireAdministrator is the
// cleanest way to handle elevation – Windows will show the UAC prompt before the
// process starts and IsUserAnAdmin() will always return true inside Main().
// Without a manifest the runtime elevation fallback below is used instead.

using System;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Windows.Forms;

namespace Triquetra
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Global exception handler so crashes show a dialog instead of
            // silently closing the window. Deliberately uses the native
            // MessageBox here, not ThemedMessageBox: UnhandledException can
            // fire on any background ThreadPool (MTA) thread, and a native
            // MessageBox is a plain user32.dll call that works regardless of
            // apartment state, whereas ThemedMessageBox creates real WinForms
            // controls that expect an STA thread - not a safe trade at the
            // one moment reliability matters most.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender,
                System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("Unhandled error:\n\n" + e.Exception.ToString(),
                    "Triquetra – Fatal Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender,
                UnhandledExceptionEventArgs e)
            {
                MessageBox.Show("Fatal error:\n\n" + e.ExceptionObject.ToString(),
                    "Triquetra – Fatal Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            try
            {
                // Hide console window if launched from cmd/powershell
                NativeMethods.ShowWindow(NativeMethods.GetConsoleWindow(),
                    NativeMethods.SW_HIDE);

                ServicePointManager.ServerCertificateValidationCallback =
                    delegate(object s,
                        System.Security.Cryptography.X509Certificates.X509Certificate c,
                        System.Security.Cryptography.X509Certificates.X509Chain ch,
                        System.Net.Security.SslPolicyErrors e) { return true; };
                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;

                // Must be set before any web request, same as SecurityProtocol
                // above but easier to miss: .NET Framework defaults
                // ServicePointManager.DefaultConnectionLimit to 2 for a non-
                // ASP.NET process, and it only affects ServicePoint objects
                // created AFTER this line runs - a ServicePoint for a host
                // that's already been contacted once keeps whatever limit was
                // in effect at that first contact, forever, no matter what
                // DefaultConnectionLimit is changed to later. AcceleratedDownload
                // opens AccelTotalConnections connections per mirror; this was
                // missing entirely when that code was ported from DeploymentTool
                // (which sets the equivalent in its own Program.Core.cs for the
                // same reason) - without it, most of those connections are never
                // actually open, just queued invisibly inside GetResponse()
                // waiting for one of 2 real slots, each eventually timing out
                // and retrying into the same contested pool. That starvation is
                // what a "both mirrors went silent" watchdog stall looks like
                // from the outside, even though neither mirror is the problem.
                // AcceleratedDownload.RunAttempt also sets ConnectionLimit
                // directly on the two specific ServicePoints it's about to use,
                // belt-and-suspenders, since Head()/hash-sidecar checks earlier
                // in the same attempt already created those ServicePoint objects.
                ServicePointManager.DefaultConnectionLimit = Math.Max(
                    ServicePointManager.DefaultConnectionLimit,
                    AcceleratedDownload.AccelTotalConnections + 4);

                if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                {
                    ThemedMessageBox.Show(null, "This program runs on Windows only.",
                        "Unsupported", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!IsAdmin())
                {
                    DialogResult r = ThemedMessageBox.Show(null,
                        "Triquetra must run as Administrator.\n\n" +
                        "Relaunch with elevated privileges?",
                        "Administrator required",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r == DialogResult.Yes)
                        ElevateAndExit();
                    return;
                }

                OsCheck.CheckNotServerOs();

                // Load config and resolve the effective theme BEFORE any
                // window/visual-styles initialisation - SetPreferredAppMode
                // must be called before Application.EnableVisualStyles() for
                // native controls to pick up the dark-mode hint at all.
                AppConfig.Load();
                Theme.IsDark = AppConfig.Theme == "dark"  ? true
                             : AppConfig.Theme == "light" ? false
                             : RegistryHelper.IsDarkMode();

                // No manifest, so Windows treats this process as DPI-unaware
                // by default and would bitmap-stretch it on a scaled display
                // unless we opt in here, before any window is created.
                try { NativeMethods.SetProcessDPIAware(); } catch { }

                NativeMethods.SetPreferredAppMode(Theme.IsDark);
                NativeMethods.FlushMenuThemes();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(null, "Startup error:\n\n" + ex.ToString(),
                    "Triquetra – Fatal Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Use WindowsPrincipal – more reliable than the shell32 P/Invoke
        // which can return wrong results depending on UAC virtualisation state.
        public static bool IsAdmin()
        {
            try
            {
                WindowsIdentity  id  = WindowsIdentity.GetCurrent();
                WindowsPrincipal pri = new WindowsPrincipal(id);
                return pri.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static void ElevateAndExit()
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                // Use ProcessStartInfo with Verb="runas" – this correctly triggers
                // UAC and works even when ShellExecuteW would suppress the prompt.
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.Verb            = "runas";
                psi.UseShellExecute = true;   // required for "runas" verb
                // Pass original args (skip argv[0] which is the exe path itself)
                string[] cmdArgs = Environment.GetCommandLineArgs();
                if (cmdArgs.Length > 1)
                {
                    string[] rest = new string[cmdArgs.Length - 1];
                    Array.Copy(cmdArgs, 1, rest, 0, rest.Length);
                    psi.Arguments = string.Join(" ",
                        Array.ConvertAll(rest, a => "\"" + a + "\""));
                }
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(null, "Could not relaunch as administrator:\n\n" + ex.Message,
                    "Elevation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            Environment.Exit(0);
        }
    }
}
