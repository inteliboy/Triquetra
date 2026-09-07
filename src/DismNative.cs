// ============================================================
// DismNative.cs
// Direct P/Invoke bindings to DismApi.dll ("DISM API Framework",
// C:\Windows\System32\DismApi.dll - ships in-box with Windows, no
// ADK required, confirmed present via VersionInfo). This is the
// same documented public native library dism.exe itself is a thin
// CLI shell over - same "P/Invoke the real library instead of
// shelling out" move UUPDumpGUI/LenovoRepoBuilder already made for
// wimlib/cabinet.dll.
//
// Signatures/struct layouts verified against Microsoft's DISM API
// reference AND cross-checked against jeffkl/ManagedDism (MIT
// licensed, actively maintained, real production C# wrapper for
// this exact API) - not guessed.
//
// Known limitation, confirmed in both sources: DismAddPackage's own
// documented remarks state "Only .cab files can be added to an
// online image. Either .cab or .msu files can be added to an
// offline image." Since Triquetra only ever services the ONLINE
// (live running) image, whether a real .msu/.esd is actually
// accepted here in practice is unverified without a live install
// attempt - DismHelper.cs's caller falls back to dism.exe on ANY
// failure here, which makes that uncertainty safe rather than a
// blocker.
//
// ResetBase (/StartComponentCleanup /ResetBase) has NO documented
// public export, but a real one exists: "_DismCleanImage"
// (underscore-prefixed - confirmed by dumping DismApi.dll's actual
// PE export table, and confirmed as the real entry point
// jeffkl/ManagedDism itself calls via EntryPoint="_DismCleanImage").
// ============================================================
using System;
using System.Runtime.InteropServices;

namespace Triquetra
{
    static class DismNative
    {
        // DISM_ONLINE_IMAGE - the exact sentinel string DismOpenSession
        // expects in place of a real mounted-image path to service the
        // live running OS. Verified against dismapi.h, not guessed.
        const string DISM_ONLINE_IMAGE = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";

        // DismCleanImageType.Component / DismCleanImageFlags.ResetBase -
        // matches "/Cleanup-Image /StartComponentCleanup /ResetBase".
        const uint DISM_CLEAN_TYPE_COMPONENT   = 4;
        const uint DISM_CLEAN_FLAGS_RESET_BASE = 1;

        enum DismLogLevel { Errors = 0, ErrorsWarnings = 1, ErrorsWarningsInfo = 2 }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate void DismProgressCallbackNative(uint current, uint total, IntPtr userData);

        static class NativeMethods
        {
            [DllImport("DismApi.dll", CharSet = CharSet.Unicode)]
            public static extern int DismInitialize(DismLogLevel logLevel, string logFilePath, string scratchDirectory);

            [DllImport("DismApi.dll", CharSet = CharSet.Unicode)]
            public static extern int DismShutdown();

            [DllImport("DismApi.dll", CharSet = CharSet.Unicode)]
            public static extern int DismOpenSession(string imagePath, string windowsDirectory, string systemDrive, out IntPtr session);

            [DllImport("DismApi.dll", CharSet = CharSet.Unicode)]
            public static extern int DismCloseSession(IntPtr session);

            [DllImport("DismApi.dll", CharSet = CharSet.Unicode)]
            public static extern int DismAddPackage(
                IntPtr session, string packagePath,
                [MarshalAs(UnmanagedType.Bool)] bool ignoreCheck,
                [MarshalAs(UnmanagedType.Bool)] bool preventPending,
                IntPtr cancelEvent, DismProgressCallbackNative progress, IntPtr userData);

            // Undocumented but real (confirmed via a PE export dump of the
            // real DLL on this machine) - same entry point
            // jeffkl/ManagedDism calls for its own CleanImage wrapper.
            [DllImport("DismApi.dll", EntryPoint = "_DismCleanImage", CharSet = CharSet.Unicode)]
            public static extern int DismCleanImage(
                IntPtr session, uint cleanImageType, uint cleanImageFlags,
                IntPtr cancelEvent, DismProgressCallbackNative progress, IntPtr userData);

            [DllImport("DismApi.dll", CharSet = CharSet.Unicode)]
            public static extern int DismGetPackages(IntPtr session, out IntPtr packageArray, out uint count);

            [DllImport("DismApi.dll", CharSet = CharSet.Unicode)]
            public static extern int DismGetLastErrorMessage(out IntPtr errorMessage);

            [DllImport("DismApi.dll", CharSet = CharSet.Unicode)]
            public static extern int DismDelete(IntPtr dismStructure);
        }

        static bool _initialized;
        static readonly object _lock = new object();

        static void EnsureInitialized()
        {
            lock (_lock)
            {
                if (_initialized) return;
                int hr = NativeMethods.DismInitialize(DismLogLevel.ErrorsWarnings, null, null);
                if (hr != 0)
                    throw new Exception("DismInitialize failed: 0x" + hr.ToString("X8"));
                _initialized = true;
            }
        }

        // Reads the per-thread error message DISM API sets immediately
        // after a failing call - must be called before any other DISM API
        // call on this thread overwrites it.
        public static string GetLastErrorMessage()
        {
            IntPtr strPtr;
            if (NativeMethods.DismGetLastErrorMessage(out strPtr) != 0 || strPtr == IntPtr.Zero)
                return null;
            try
            {
                // DismString is { PCWSTR Value; } - a single pointer field.
                IntPtr valuePtr = Marshal.ReadIntPtr(strPtr);
                return valuePtr == IntPtr.Zero ? null : Marshal.PtrToStringUni(valuePtr);
            }
            finally { NativeMethods.DismDelete(strPtr); }
        }

        static int WithOnlineSession(Func<IntPtr, int> body)
        {
            EnsureInitialized();
            IntPtr session;
            int hr = NativeMethods.DismOpenSession(DISM_ONLINE_IMAGE, null, null, out session);
            if (hr != 0)
                throw new Exception("DismOpenSession failed: 0x" + hr.ToString("X8"));
            try { return body(session); }
            finally { NativeMethods.DismCloseSession(session); }
        }

        static DismProgressCallbackNative WrapProgress(Action<string> progressCb)
        {
            if (progressCb == null) return null;
            return delegate(uint current, uint total, IntPtr userData)
            {
                double pct = total > 0 ? current * 100.0 / total : 0;
                progressCb(pct.ToString("F1") + "%");
            };
        }

        // Returns the raw DISM HRESULT (0/3010/3011 = success per DismRc;
        // anything else = failure). Throws only for session/init
        // infrastructure failures - a normal package rejection is a
        // returned failure code, not an exception. Callers must check
        // DismRc.IsSuccess and fall back to the CLI path otherwise.
        public static int AddPackage(string packagePath, Action<string> progressCb)
        {
            DismProgressCallbackNative nativeCb = WrapProgress(progressCb);
            int rc = WithOnlineSession(delegate(IntPtr session)
            {
                return NativeMethods.DismAddPackage(session, packagePath,
                    false, false, IntPtr.Zero, nativeCb, IntPtr.Zero);
            });
            GC.KeepAlive(nativeCb);
            return rc;
        }

        public static int ResetBase(Action<string> progressCb)
        {
            DismProgressCallbackNative nativeCb = WrapProgress(progressCb);
            int rc = WithOnlineSession(delegate(IntPtr session)
            {
                return NativeMethods.DismCleanImage(session,
                    DISM_CLEAN_TYPE_COMPONENT, DISM_CLEAN_FLAGS_RESET_BASE,
                    IntPtr.Zero, nativeCb, IntPtr.Zero);
            });
            GC.KeepAlive(nativeCb);
            return rc;
        }

        // Returns the number of packages currently in the online image, or
        // -1 on failure. Used only as a diagnostic "refresh servicing
        // state" probe (mirrors the existing "dism /Online /Get-Packages"
        // CLI step) - Triquetra doesn't need per-package details today, so
        // the returned array's contents are freed immediately, unread.
        public static int GetPackageCount()
        {
            int count = -1;
            WithOnlineSession(delegate(IntPtr session)
            {
                IntPtr arrayPtr; uint n;
                int hr = NativeMethods.DismGetPackages(session, out arrayPtr, out n);
                if (hr == 0)
                {
                    count = (int)n;
                    NativeMethods.DismDelete(arrayPtr);
                }
                return hr;
            });
            return count;
        }
    }
}
