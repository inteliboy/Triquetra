// ============================================================
// NativeMethods.cs
// P/Invoke helpers: console show/hide (original) plus dark-mode
// window chrome (title bar, window border, native scrollbars),
// ported from LenovoRepoBuilder\src\NativeMethods.cs (itself ported
// from UUPDumpGUI\Core.cs) - see those projects' CLAUDE.md "Visual
// overhaul" sections for the live-tested history behind these
// specific attribute numbers/ordinals.
// ============================================================
using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Triquetra
{
    static class NativeMethods
    {
        public const int SW_SHOWNORMAL = 1;
        public const int SW_HIDE       = 0;

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        // Undocumented, ordinal-only (stable since Win10 1903 for these three -
        // AllowDarkModeForWindow=133, SetPreferredAppMode=135, FlushMenuThemes=136,
        // per the well-known uxtheme.dll ordinal table) - best-effort, wrapped in
        // try/catch by every caller here.
        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        static extern int SetPreferredAppModeInternal(int preferredAppMode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        static extern void FlushMenuThemesInternal();

        // Applies dark OR light scrollbar theming to one native scrollable
        // control (ListBox/TextBox/RichTextBox). Order matters: call
        // AllowDarkModeForWindow before SetWindowTheme. Best-effort since
        // this is an undocumented API. Must be called again on every theme
        // toggle, not just once at handle creation - otherwise a scrollbar
        // can stay dark forever after a single dark->light toggle.
        public static void SetScrollBarTheme(IntPtr hwnd, bool dark)
        {
            try { AllowDarkModeForWindow(hwnd, dark); } catch { }
            try { SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        // Call once at startup, before Application.EnableVisualStyles().
        public static void SetPreferredAppMode(bool dark)
        {
            try { SetPreferredAppModeInternal(dark ? 1 : 0); } catch { }
        }

        public static void FlushMenuThemes()
        {
            try { FlushMenuThemesInternal(); } catch { }
        }

        // DWMWA_USE_IMMERSIVE_DARK_MODE - documented, but only actually
        // honored on Windows 10 1809+. The attribute number changed once:
        // 19 on early 1809-era builds, 20 from Windows 10 20H1 onward
        // (including all of Windows 11) - try 20 first, fall back to 19.
        //
        // This alone darkens the title bar's own background/text/caption
        // buttons, but NOT the thin 1px border Windows 11 draws around the
        // whole window - that needs SetBorderAndCaptionColor below too.
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            int v = dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int));
            }
            catch { }
        }

        // COLORREF is 0x00BBGGRR, not RGB - byte order matters here.
        static int ToColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        // Windows 11 only (silently no-ops on Windows 10, where this
        // attribute doesn't exist and DwmSetWindowAttribute just fails).
        // Pass Color.Empty for either parameter to reset that one back to
        // the system default instead of setting an explicit color.
        public static void SetBorderAndCaptionColor(IntPtr hwnd, Color border, Color caption)
        {
            const int DWMWA_BORDER_COLOR = 34;
            const int DWMWA_CAPTION_COLOR = 35;
            const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
            try
            {
                int b = border.IsEmpty ? DWMWA_COLOR_DEFAULT : ToColorRef(border);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref b, sizeof(int));
            }
            catch { }
            try
            {
                int c = caption.IsEmpty ? DWMWA_COLOR_DEFAULT : ToColorRef(caption);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref c, sizeof(int));
            }
            catch { }
        }
    }
}
