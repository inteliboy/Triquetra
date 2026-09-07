## Approach
- Read existing files before writing. Don't re-read unless changed.
- Thorough in reasoning, concise in output.
- Skip files over 100KB unless required.
- No sycophantic openers or closing fluff.
- No emojis or em-dashes.
- Do not guess APIs, versions, flags, commit SHAs, or package names. Verify by reading code or docs before asserting.
- Test before assuming something works
- For UI changes do a fine-grid analysis or placement, aligment, text not being cut.
- Update this file with useful information for future work, mirroring `C:\Users\inteliboy\Desktop\UUPDumpGUI\CLAUDE.md`/`C:\Users\inteliboy\Desktop\LenovoRepoBuilder\CLAUDE.md`'s level of detail.

## Project overview
C#5/.NET Framework 4.8 WinForms tool (`src\*.cs`, all files compiled together by one `csc.exe` invocation in `build.cmd`, `/platform:x64` - no external UI framework). Downloads Windows 11 CAB/ESD/MSU packages from a user-configured HTTP mirror (Base URL/Username/Password, set in the Settings tab - no default baked in, deliberately) matching the local machine's build branch, installs them via DISM, and offers Enablement Package / DISM ResetBase / reboot helpers. Runs elevated (UAC manifest embedded at compile time by `build.cmd`, plus a runtime elevation-prompt fallback in `Program.cs` for the no-manifest case). Ships as a single `triquetra.exe` in the project root (checksums alongside it) - no embedded resources/DLLs, unlike the sibling projects.

Files: `Program.cs` (entry point, admin check/elevation) `Models.cs` (`VersionInfo`) `NativeMethods.cs` (console show/hide + dark-mode P/Invoke) `OsCheck.cs` `Logger.cs` `AppConfig.cs` (`triquetra.conf` INI-style settings, now also the theme override) `RegistryHelper.cs` `Net.cs` (+ `RemoteHash`) `HtmlParser.cs` `SelfUpdater.cs` `DismHelper.cs` `EnablementPackage.cs` `VersionCompare.cs` (+ `DismRc`) - each a straight cut from the old single-file `triquetra.cs`, no logic changes. `Theme.cs` (`Theme`, `Ctrl` - palette + control factory) `Dialogs.cs` (`FlatProgressBar`, `ThemedMessageBox`) `ThemedControls.cs` (`ThemedCheckBox`) `MainForm.cs` (the UI) are the dark/light theme visual-overhaul work, ported from `LenovoRepoBuilder`'s already-verified version of the same pattern (see that project's CLAUDE.md "Visual overhaul" section for the deeper history of *why* each native-control workaround exists - not re-derived here).

`triquetra.cs.orig_backup` (root) is the pre-split monolithic source, kept as a reference/safety net only - not part of the build. `build.ps1.orig_backup` is the old PowerShell build script (compiled from the monolith, signed, and deployed the exe to an internal network share) - superseded by `build.cmd`, kept only for reference. Neither is read by `build.cmd`. **Both still contain the old hardcoded mirror URL/credentials/deploy path removed from the active source below - if these backups are ever shared outside this machine, scrub or drop them first.**

## Versioning (modeled on UUPDumpGUI)
`version.txt` holds `MAJOR.MINOR.BUILD.REVISION`; `build.cmd` bumps REVISION only on every successful compile and regenerates `src\AssemblyVersion.generated.cs` (2 lines, a build output - not hand-edited, but still globbed in by `src\*.cs`). `MainForm`'s displayed version is `Assembly.GetExecutingAssembly().GetName().Version.ToString()`, read once - no more regex-patching a version string into a `.cs` file on every build (the old `build.ps1` did this; deleted entirely). Bump major/minor by hand-editing `version.txt`; the build only ever touches the last number. Currently at 3.0.0.x, bumped from 2.0.6.x for this restructure (matches `LenovoRepoBuilder`'s own convention of bumping to 3.0.0.0 for its equivalent rewrite).

## build.cmd
Modeled on `LenovoRepoBuilder\build.cmd` (locate `csc.exe` under `Framework64` first, required-file checks) with signing/checksum steps ported from the old `build.ps1`: signs with `signtool.exe` (hardcoded path, cert name `SMCE`, best-effort - warns and continues if signtool itself is missing, hard-fails if signing fails) and writes `triquetra.exe.md5`/`.sha256` via `certutil -hashfile` (no PowerShell dependency, unlike the old script). The old script's Step 7 (map a drive letter, copy exe+checksums to an internal network share) is gone entirely, per explicit request - **nothing in this repo deploys anywhere now.**
- Still embeds a `requireAdministrator` UAC manifest at compile time (same temp-file approach as the old script, translated to batch `>` redirection) - this is what makes Windows show the UAC prompt before the process starts; `Program.IsAdmin()`/`ElevateAndExit()` remain as the runtime fallback for a build that somehow lacks the manifest.
- `certutil -hashfile ... | findstr /v /i "hash"` relies on BOTH the header line ("... hash of file ...") and the footer line ("CertUtil: -hashfile command completed successfully.") containing the substring "hash" - filtering them out leaves exactly the hex hash line. Do NOT add a `for /f skip=1` on top of this - that was tried and broke it (skips the one line that's left after filtering already removed both header and footer).

## No hardcoded mirror/credentials - user must configure a Base URL
`AppConfig.BaseUrl`/`User`/`Password` all default to `""` (previously baked-in defaults
pointing at a specific private server, plus that server's credentials, removed
entirely from source per explicit request). Nothing works - Refresh, Install, the
Enablement Package download, or the self-update check - until the user fills in a
real Base URL (and credentials, if the mirror needs them) on the Settings tab; every
call site that needs a URL (`MainForm.FetchBuildsInner`, `EnablementPackage.CheckAndOffer`,
the self-update check in `MainForm.StartupTasks`) checks `string.IsNullOrEmpty(AppConfig.BaseUrl)`
first and fails with a clear log/status message instead of guessing. The Enablement
Package cab paths (`EnablementPackage.cs`) are stored as paths relative to `BaseUrl`
(`EP/<arch>/<file>`), resolved at use time, not as absolute URLs baked in at
construction - so they follow whatever mirror the user configures, live.
`Ctrl.MakeEntry(true)` (password-masked) is now used for **both** Username and
Password fields, not just Password.

There used to also be a `Failsafe` toggle that swapped in a hardcoded fallback IP
mirror and skipped self-update. That whole mechanism (fallback IP + the
`Net.ChooseFastestMirror` dual-mirror "test both, pick the faster one" logic it
depended on) is gone - `AppConfig.DisableSelfUpdate` (Settings tab: "Disable
self-update") now does exactly what its name says and nothing else. Fetching still
always uses the one configured `BaseUrl` directly, no mirror racing.

## Self-update vs. local testing - a real trap (still applies, mechanism unchanged)
`SelfUpdater.TryUpdate` (called from `MainForm.StartupTasks` unless
`AppConfig.DisableSelfUpdate` or `BaseUrl` is empty) always compares the running exe's
hash against `<BaseUrl>/triquetra.exe` and, on any mismatch, downloads the REMOTE copy,
overwrites the local exe **in place** (`Copy-Item -Force` onto
`Process.GetCurrentProcess().MainModule.FileName`, i.e. this project's own
`triquetra.exe`), and relaunches - via a detached PowerShell script that keeps running
even after the original process is killed.
- **This bit hard during an earlier session's own testing**: launching a freshly-built
  exe (with a local-only version bump, so its hash never matches the remote one)
  silently swapped it for whatever old build the remote server happens to host and
  relaunched THAT - every screenshot/pixel-check taken without accounting for this was
  actually of the wrong binary, not the code just built. Caught only by noticing the
  window title's version string didn't match what was just compiled.
- **Before ever testing a local build interactively**: set `disable_self_update = true`
  in `C:\ProgramData\triquetra\triquetra.conf` first (skips `SelfUpdater.TryUpdate`
  entirely - there's no longer a second fallback-mirror side effect to also worry
  about). Restore the config's original value afterward - it's live user-machine state,
  not part of the repo. If `BaseUrl` is also blank, self-update is already a no-op
  regardless of this flag.
- **Consequence for shipping a real build against a real mirror**: whatever server a
  user points `BaseUrl` at needs its own hosted `triquetra.exe` kept in sync with
  whatever gets built, or the very first launch will self-update back down to
  whatever that server currently hosts. This isn't something the code needs to guard
  against - it's the mechanism working as designed - but it means a fresh local build
  is never "deployed" just by building it.

## Visual overhaul (Theme.cs/Ctrl, Dialogs.cs, ThemedControls.cs, MainForm.cs)
Ported from `LenovoRepoBuilder`'s already-verified `Theme`/`Ctrl`/`NativeMethods`/`Dialogs`/`ThemedControls` (see that project's CLAUDE.md for the full history of *why* each workaround exists - not re-derived here). Triquetra started from the "known, disclosed gap" state both sibling projects begin from: a `Theme`/`Ctrl` pair and a runtime toggle button existed, but `ApplyTheme()` only recolored `BackColor`/log box/build list/status label - buttons, checkboxes, textbox/listbox/richtextbox borders, native scrollbars, the tab strip chrome, the title bar/border, and all 17 `MessageBox.Show` call sites were unthemed. All of that is now closed:
- **Title bar/border**: `NativeMethods.SetDarkTitleBar`/`SetBorderAndCaptionColor`, called from `OnHandleCreated` and `ApplyTheme` (same DWM attributes as both sibling projects, not re-derived).
- **Tab strip** (3 tabs, 2 inter-tab gaps): `TabAppearance.FlatButtons` + `OwnerDrawFixed` + `DrawTab`, sized from the widest real label. Six overlay panels (`_tabRowFill`/`_tabFrameTop/Bottom/Left/Right`/`_tabGroupTop`) plus a **looped** `_tabInterGapFills[]` (Lenovo's generalized version, not UUPDump's hardcoded-2-tab one) cover native chrome `BackColor` can't reach.
- **Native scrollbars**: `NativeMethods.SetScrollBarTheme` applied to `_buildList`/`_logBox` from both `OnHandleCreated` and `ApplyTheme` - must be re-applied on every toggle, not just once, or a scrollbar themed dark at startup stays dark forever after a toggle to light.
- **TextBox/ListBox/RichTextBox borders**: `Ctrl.Bordered(Control)` wraps each in a themed-border Panel (tagged `"theme-border-wrap"`) - extended beyond the sibling projects' version to also accept `RichTextBox` (the log box needs it too, and `RichTextBox.BorderStyle` exists the same as `TextBox`'s).
- **CheckBoxes**: `Ctrl.MakeCheck` now returns a `ThemedCheckBox` (owner-drawn - native CheckBox always paints its check glyph via UxTheme regardless of theme).
- **ProgressBars**: both `_spinBar` (marquee) and `_dlBar` (continuous) are now `FlatProgressBar` - native ones ignore this app's manual theme override.
- **MessageBox -> ThemedMessageBox**: all 17 call sites switched. The two top-level crash handlers in `Program.cs` (`Application.ThreadException`/`AppDomain.UnhandledException`) were deliberately left on the **native** `MessageBox.Show`, not `ThemedMessageBox` - `UnhandledException` can fire on a background ThreadPool (MTA) thread, and `ThemedMessageBox` creates real WinForms controls that expect an STA thread, while native `MessageBox.Show` is just a `user32.dll` call that works regardless of apartment state. Not a safe trade at the one moment reliability matters most.
- **Full `ApplyTheme()` rewrite**: tag-based `RestyleEntries` (`"theme-bg"`/`"theme-surface"`/`"theme-border-wrap"`/`"theme-normal-btn"`) recolors every panel/button/textbox/label recursively; `RecolorLog` re-tints already-appended log lines by content on every toggle, not just new ones.
- **Theme persistence**: `AppConfig.Theme` (`"system"`/`"dark"`/`"light"`), saved to the existing `triquetra.conf` (not the registry, unlike `LenovoRepoBuilder` - this app already has a config file, no reason to add a second persistence mechanism). `Program.Main` resolves the effective `Theme.IsDark` from this (falling back to `RegistryHelper.IsDarkMode()` for `"system"`) BEFORE `Application.EnableVisualStyles()` - `NativeMethods.SetPreferredAppMode` must be called before that or native controls never pick up the dark-mode hint at all, same ordering both sibling projects use.

### Two real bugs found and fixed only by live pixel-testing (not by reading the code)
- **`Ctrl.Bordered` wrapper panels need an explicit Height.** A bare `new Panel()` defaults to `Height=100` - wrapping each Settings-tab `TextBox` in `Ctrl.Bordered(tb)` with only `Dock.Fill` set (no explicit Height) balloons that `TableLayoutPanel` row to 100px instead of the entry's own ~24px, since `Dock.Fill` only fills whatever height the row already decided on, it doesn't derive one from the wrapped content. Fixed in `AddSettingRow` with `wrap.Height = tb.PreferredHeight + 2;` (the `+2` for the 1px top/bottom border padding). Any future `Ctrl.Bordered(...)` call site needs to size the returned wrapper explicitly the same way - it's documented as "the caller's responsibility" in `Ctrl.Bordered`'s own comment, easy to forget in practice.
- **`ThemedCheckBox` painted garbled overlapping text when its parent's `BackColor` was `Color.Transparent`.** Its `OnPaint` filled its own background from `Parent.BackColor` - correct when the parent is opaque, but the Settings tab's checkboxes sit in a `FlowLayoutPanel` that's deliberately `Color.Transparent` (so it shows through to the tab's own `Theme.Bg`). Filling a rectangle with a literal `Color.Transparent` brush paints nothing at all, leaving whatever was drawn there before (a neighboring checkbox's text, mid-invalidation) visible underneath - looked like double-exposed overlapping text, not a sizing bug. Fixed by walking up the parent chain to the nearest ancestor with a real (non-transparent) `BackColor` instead of trusting the immediate `Parent`.
- Separately (not a bug, but a related gotcha): `CheckBox.AutoSize` (true by default) does NOT size around `ThemedCheckBox`'s own custom box+text layout - every checkbox label truncated to a few characters plus "..." regardless of the checkbox's real (long) text. A `GetPreferredSize` override alone did not fix this (the FlowLayoutPanel's AutoSize path doesn't appear to consult it for this control). Fixed by disabling `AutoSize` entirely in `Ctrl.MakeCheck` and setting `Width`/`Height` explicitly from `TextRenderer.MeasureText`, mirroring the same "measure real text, don't guess" pattern already used by `Ctrl.TextButtonHeight`/`MeasureWrappedHeight`.
- **General lesson reaffirmed**: neither of the two bugs above was visible from reading the code - both were only caught by actually building, launching, and pixel-sampling (`Bitmap.GetPixel` / a full scanline color-transition scan, never eyeballing a screenshot thumbnail - a coarse read at the wrong y-coordinate produced a false "bug" once, in the OS caption-bar/DWM-Mica region, before the real bugs above were found in the actual client area).
- **Log tab toolbar: "Copy all" sat 3px lower than "Clear window"/"Clear log file"** (user-reported, confirmed via a per-column vertical scanline scan: the other two buttons' fill started at y=121, "Copy all"'s at y=124). Root cause: `bCW`/`bCF` each got `Margin = new Padding(0, 0, 6, 0)` explicitly, but the third button (`bCA`) never had its `Margin` touched at all, leaving it at `Control`'s default `Padding(3,3,3,3)` - the extra 3px top margin against its siblings' zeroed one shifted it down by exactly 3px in the `FlowLayoutPanel` row. Fixed by explicitly zeroing `bCA.Margin` too. **Generalizes**: any `FlowLayoutPanel`/side-by-side row where some sibling controls get an explicit `Margin` and one is left at the factory default is exactly this bug waiting to happen - always set `Margin` on every control in a row together, never on all-but-one.

## Native DISM API path (DismNative.cs) - alternative to shelling out to dism.exe
`DismHelper.AddPackage`/`ResetBase`/`RefreshPackageState` each check `AppConfig.UseDismApi`
(Settings tab checkbox "Use DISM API instead of dism.exe", default off, persisted as
`use_dism_api` in `triquetra.conf`) and, if true, try the native path first - on ANY
failure (bad HRESULT or a thrown exception) they log it and transparently fall back to
the original `dism.exe`/`powershell.exe` CLI implementation (renamed `*Cli` internally),
so the CLI path is never removed, just no longer the only option. `DismRc.IsSuccess`/
`NeedsReboot` (`src/VersionCompare.cs`) work unchanged for the native path too - confirmed
`DismAddPackage`/`_DismCleanImage` return the literal `3010`/`3011` codes, not
HRESULT-encoded.

`DismNative.cs` P/Invokes `C:\Windows\System32\DismApi.dll` ("DISM API Framework", ships
in-box, no ADK needed - dism.exe itself is a thin CLI shell over this same library).
Signatures/struct layouts were cross-checked against **jeffkl/ManagedDism** (MIT,
actively maintained, real production C# wrapper - `github.com/jeffkl/ManagedDism`), not
guessed from docs alone.
- **Confirmed by dumping the real DLL's PE export table** (136 exports, via a throwaway
  `csc`-compiled reader): exactly one `DismAddPackage` export - cab/msu/esd genuinely go
  through the same single function, not separate ones.
- **`DismAddPackage`'s own doc remarks say "Only .cab files can be added to an online
  image. Either .cab or .msu files can be added to an offline image."** ManagedDism's
  2024-era source repeats this verbatim - a real, persisted constraint, not stale
  documentation. Triquetra only ever services the ONLINE image. **Whether a real
  `.msu`/`.esd` is actually accepted here in practice is still unverified** - `dism.exe`
  installs them online every day, so it likely pre-extracts `.msu`'s inner `.cab` (or
  something equivalent) before ever reaching this exact entry point, which the public API
  doesn't expose either way. Not resolved by this work - the automatic CLI fallback is
  what makes that safe to leave unverified rather than a blocker.
- **ResetBase has no *documented* public export, but a real callable one exists**:
  `_DismCleanImage` (underscore-prefixed - confirmed in the same PE export dump), called
  with `DismCleanImageType.Component` (=4) and `DismCleanImageFlags.ResetBase` (=1) -
  exactly `/Cleanup-Image /StartComponentCleanup /ResetBase`. This is the same entry
  point ManagedDism itself calls via `EntryPoint = "_DismCleanImage"` for its own
  `CleanImage` wrapper - not something this session invented.
- **Verified live, safely, without any real system-modifying install** (a real `.msu`/
  `.esd` install or any real ResetBase run is exactly as destructive via the native path
  as the CLI one, so neither was triggered - same rule as every other live test this
  project's sessions have followed):
  - `DismNative.GetPackageCount()` (`Initialize -> OpenOnlineSession -> DismGetPackages ->
    CloseSession`) returned **295** real packages from this machine's live image with no
    errors - proves the full session lifecycle and P/Invoke plumbing work end-to-end.
    Cross-checking against `dism /Online /Get-Packages`'s own human-readable output
    counted only **233** `"Package Identity :"` entries - a real, unexplained difference
    (not a marshaling bug on Triquetra's side: `count` is the raw `uint` DismGetPackages
    itself returned, no struct parsing involved). The two evidently don't enumerate
    identically by default; since `RefreshPackageState`/`GetPackageCount` only log the
    number as an FYI pulse-check and never act on its exact value, this doesn't block
    anything, but don't assume the two are interchangeable if a future need ever wants an
    exact package list/count.
  - `DismNative.AddPackage` called against a deliberately-invalid dummy file (empty text
    renamed `.cab`) correctly returned a real failure HRESULT (`0xCA00A005`) rather than
    crashing or hanging - `GetLastErrorMessage()` returned `null` for this particular
    failure (DISM doesn't always set a per-thread message, evidently), but the non-zero
    return code alone is exactly what `DismHelper`'s fallback check
    (`DismRc.IsSuccess(rc)`) needs to trigger the CLI fallback correctly.
- GC lesson (already documented in `UUPDumpGUI`'s CLAUDE.md, applies here identically):
  the `DismProgressCallbackNative` delegate passed to native code needs `GC.KeepAlive()`
  after the native call returns - `DismNative.AddPackage`/`ResetBase` both do this.

## Accelerated (hedged multi-mirror) download - AcceleratedDownload.cs
Opt-in, off by default (`AppConfig.AccelDownloads`, Settings tab "Enable
accelerated download" - unlocks "Mirror server"/"Hedge budget %" rows only
while checked). Ported/scoped down from our `DeploymentTool` project's
`updater\Program.AcceleratedDownload.cs` (~2000 lines, read in full via a
research fork before porting - not re-derived from memory). `Net.DownloadFile`
tries it first when enabled and transparently falls back to its own
single-connection path on anything short of full success - same
try-new-path-then-fall-back-to-proven-path shape as `DismHelper`'s native-API
toggle, no caller of `DownloadFile` needed any changes.

**Mechanics**: splits a file into 16MB chunks, fetches them over 8 parallel
ranged HTTP connections split between `BaseUrl` and `MirrorUrl` proportional to
a one-time-per-session speed probe, and "hedges" (starts a redundant fetch on
the other mirror) any chunk whose owner looks `>=1.75x` slower than the other
mirror's projected ETA, gated by the user-configured hedge-budget percent
(0 = hedging off). Whichever side finishes first wins via an atomic phase
claim; the loser is discarded. A single writer thread commits chunks to disk
strictly in order. Eligibility requires both mirrors to agree on file
size/Last-Modified (within 2s) and hash sidecar (reusing `Net.FetchRemoteHash`
as-is) before any bytes are split - a disagreeing/misconfigured mirror just
means "not eligible, use the plain path," never an error. A 15s
no-bytes-from-either-mirror stall watchdog tears down and restarts an attempt
(up to 3 total) before giving up. **Deliberately not ported**: DeploymentTool's
checkpoint/resume-across-process-restart (`.accel-checkpoint`) - that exists
because it writes to flaky USB media in WinPE with real crash/reboot risk;
Triquetra writes to a normal running Windows install, and its existing plain
path has never had resume either. An interrupted accelerated download just
restarts from scratch next time.

**Simplified from DeploymentTool's hedge-resolution**: no explicit byte-compare
of the overlapping range on a genuine owner/hedge tie. A hedge win here takes
the owner's own already-fetched PREFIX (bytes it finished before the hedge
ever started - never touched again afterward, so safe to read without racing
the owner's still-in-flight fetch of the rest) plus the hedge's own TAIL, as
two separate sequential writes - never splicing bytes between two buffers that
could still be concurrently written to, which is what a splice-into-shared-
buffer approach (closer to DeploymentTool's own design) would risk. Real
mirror byte-level disagreement is still caught, just later - by the mandatory
whole-file hash check every attempt ends with, not per-chunk during hedging.

**Verified live against a real 2.26GB ESD, two ways**, via a standalone
harness (real `AcceleratedDownload.cs`/`Net.cs`/`AppConfig.cs`/`Logger.cs` + a
throwaway `Main`, same pattern as `DismNative.cs`'s own verification):
1. **Self-mirrored** (`MirrorUrl` set equal to `BaseUrl`, before realizing
   `updates2.smce.pl` is a real second mirror this server family actually
   has) - downloaded correctly at ~96 MB/s across 8 connections.
2. **Genuine dual-mirror** (`BaseUrl=updates.smce.pl`,
   `MirrorUrl=updates2.smce.pl` - confirmed first via HEAD that both hosts
   agree on size/Last-Modified/hash for the same real file) - downloaded
   correctly at ~170 MB/s (faster than the self-mirror run, as expected -
   two genuinely different backends contribute real additive bandwidth
   rather than hitting the same server twice).

Both runs: reconstructed file's SHA-256 exactly matched the independently-
published `.sha256` sidecar - proves chunking, concurrent fetch, ordered
reconstruction, and final verification are all correct, including chunk
offsets past the 2GB `Int32` boundary (confirmed separately that
`HttpWebRequest.AddRange(long, long)` is the real overload this machine's
.NET Framework resolves to before relying on it - large ESDs routinely exceed
2GB). The self-mirror run also triggered and correctly resolved one genuine
hedge (see below for why), proving that path live too, not just theoretically.

**Speed probe redone to match DeploymentTool's real mechanism, after the
user pointed out the self-mirror run's measured speeds looked wrong** (main
7.9 MB/s vs mirror 24.6 MB/s - for the SAME server hit twice). Checked
DeploymentTool's actual `PickServer` (`Program.ServerDiscovery.cs`) rather
than re-deriving from memory: it downloads a dedicated `speed.test` file each
mirror hosts (confirmed present on both `updates.smce.pl` and
`updates2.smce.pl`, 50MB each - this is also the exact file the OLD
pre-refactor Triquetra's removed `Net.ChooseFastestMirror` used, before this
session deleted it as dead code following the "no hardcoded server" cleanup -
that removal was correct, this is a different, still-needed mechanism), and
crucially probes **both mirrors in parallel threads**, not sequentially.
Triquetra's original probe did two things wrong at once: range-read the
actual target file's first 1MB (instead of the dedicated speed.test file) AND
probed main then mirror one after another, not in parallel - sequential
probing is inherently unfair, since the first request pays DNS/TLS/TCP-slow-
start costs the second one gets to skip via connection reuse, which is
exactly what produced the bogus 3x "difference" between two identical
mirrors. Fixed to match DeploymentTool exactly: `EnsureSpeedProbed` now
spawns one thread per mirror against `<mirror>/speed.test`, joins both, only
then computes the weighted split. Re-verified against the real two-mirror
pair: main 64.4 MB/s / mirror 111.6 MB/s - genuinely different real numbers
for genuinely different servers, and the upfront weighted allocation alone
was already good enough that no hedge was even needed for that run (0 hedge
bytes) - a sign the fix is working, not just different.

**Clarifying an exchange with the user about this**: nothing in
`DeploymentTool`'s own source was ever modified - it was only read (via a
research fork, read-only) to inform this port. The `Head()` exception-logging
addition made during the first round of testing was **not fixing a bug
inherited from DeploymentTool** - checked its actual `Head()`
(`Program.Network.cs`) directly and it has the identical bare
`catch { return false; }` with no logging; DeploymentTool's version "works
flawlessly" because its own `Program.cs`-equivalent already sets
`ServicePointManager.SecurityProtocol = Tls12` at startup, same as
Triquetra's real `Program.cs` does - the standalone *test harness* just
hadn't replicated that one-line global setting, which is what actually caused
the first test run's instant, log-free failure. The logging line added to
`Head()` is a genuine, harmless improvement on top of the ported logic, not a
correction of it.

**What's still unverified**: genuine mirror *disagreement* (two backends
actually reporting different size/timestamp/hash, or serving different bytes
for the same range during a real hedge) has no way to be tested without two
mirrors that actually disagree - both real-mirror test runs used files
already confirmed identical on both sides. The engine's mechanics are now
proven correct against a real second mirror; only the disagreement-detection
branches remain unexercised live.

## Accelerated download: ServicePointManager.DefaultConnectionLimit was never
## raised - a real, confirmed gap dropped during the DeploymentTool port
User-reported: a real accelerated download stalled and never recovered on a
live machine. Investigated by re-reading DeploymentTool's actual
`updater\Program.Core.cs` (around its `RunPipeline` entry) and
`Program.AcceleratedDownload.cs` side by side with Triquetra's
`AcceleratedDownload.cs` - not re-derived from memory. Found a real,
previously undocumented difference: DeploymentTool raises
`ServicePointManager.DefaultConnectionLimit` (process-wide, in its
`Program.cs`-equivalent, before any web request) AND additionally sets
`ServicePointManager.FindServicePoint(mirrorUri).ConnectionLimit` directly on
each of its two mirror hosts right before every accelerated transfer -
DeploymentTool's own comment explains why both are needed: .NET Framework
defaults `DefaultConnectionLimit` to **2** for a non-ASP.NET process, raising
it only affects `ServicePoint` objects created *after* that line runs, and by
the time an accelerated download starts its host's `ServicePoint` has
already been created (by the HEAD/hash-sidecar checks and the speed probe
that run first) and is locked at whatever the limit was at that first
contact - so the per-host `.ConnectionLimit` set is the only thing that can
still raise it. Neither of these existed anywhere in Triquetra's port - not
in `Program.cs`, not in `AcceleratedDownload.cs` - meaning every accelerated
download ran with a **hard cap of 2 real concurrent connections per mirror**
regardless of `AccelTotalConnections` (8) or the main/mirror connection
split `ComputeWeightedConnections` computed; the other connections just sat
queued inside `GetResponse()` waiting for one of the 2 real slots, each
eventually hitting `AccelTimeoutMs` (8s) and retrying into the same
contested pool. DeploymentTool's own comment on this describes the exact
observed symptom: *"That starvation is what a 'both mirrors went silent'
watchdog stall looks like from the outside, even though neither mirror was
actually the problem."*
- **Fixed to match DeploymentTool exactly**: `Program.cs` now raises
  `ServicePointManager.DefaultConnectionLimit` to
  `AcceleratedDownload.AccelTotalConnections + 4` (=12) at startup, right
  next to the existing `SecurityProtocol`/`ServerCertificateValidationCallback`
  setup (same "must run before any web request" ordering constraint).
  `AcceleratedDownload.RunAttempt` additionally sets
  `ServicePointManager.FindServicePoint(...).ConnectionLimit` directly on
  both the main and mirror host `Uri`s, belt-and-suspenders, since by the
  time `RunAttempt` runs those `ServicePoint`s already exist from the
  earlier `Head()`/hash-sidecar/speed-probe calls in the same attempt.
  `AccelTotalConnections` was `private`, changed to `internal const` so
  `Program.cs` can size the bump from the same constant instead of a second
  hardcoded number.
- **Verified live against the real 2.26GB ESD and the real two-mirror pair**
  (`updates.smce.pl`/`updates2.smce.pl`, real credentials from
  `C:\ProgramData\triquetra\triquetra.conf` via `AppConfig.Load()`), via a
  standalone harness compiled from the real `AcceleratedDownload.cs`/
  `Net.cs`/`AppConfig.cs`/`Logger.cs`/`HtmlParser.cs` (same pattern as this
  project's other live-verification harnesses) downloading to a throwaway
  scratch directory (never touches the real cached `.esd`/`.cab` files in
  `ProgramData`, so the cached-copy short-circuit never masks a real
  transfer) with `AppConfig.DisableSelfUpdate` forced true:
  - **Confirmed the precondition directly**: `ServicePointManager
    .FindServicePoint(mainUri).ConnectionLimit` read **2** immediately after
    `AppConfig.Load()`, before any accelerated-download code ran - the bare
    .NET Framework default, exactly as the DeploymentTool comment describes.
  - **Fixed code**: after `TryDownload` returned, both the main and mirror
    `ServicePoint.ConnectionLimit` read **12** - the per-host override took
    effect on the already-created `ServicePoint`s. Download completed in
    14.1s at up to ~202 MB/s (peak) / ~160 MB/s (average), 0 hedge bytes
    needed, hash verified OK.
  - **Negative control - same file, same live mirrors, same process
    settings, only the per-host `ConnectionLimit` override removed** (a
    throwaway copy of the pre-fix `AcceleratedDownload.cs`, compiled into a
    second harness exe): `ConnectionLimit` measured **2** both before and
    after the download, confirming the override really is what raises it
    and nothing else in the ported code path does. **This run still
    completed successfully at effectively the same speed (14.2s, ~198 MB/s
    peak)** - on this machine's actual link, 2 real connections per mirror
    were apparently still enough to saturate the 16MB chunks well inside the
    8s `AccelTimeoutMs`, so the starvation never escalated into the retry-
    storm/dead-mirror/stall-watchdog chain DeploymentTool's comment
    describes. **So this fix is confirmed correct and necessary to match
    DeploymentTool's own reasoning, and is a real, previously-undocumented
    regression from the port - but the live test environment's network was
    too healthy to reproduce the user's original stall on demand, so it
    cannot be called the *proven* root cause of that specific incident, only
    the most credible latent one found.** It would be expected to bite
    specifically under the conditions DeploymentTool's comment names -
    higher latency to a mirror, packet loss, or a momentary server-side
    slowdown on one side - which a healthy same-session test can't force.
  - Also relevant to recovery time regardless of this fix: `RunAttempt`'s
    `foreach (Thread w in workers) w.Join(5000)` after a stall/`GiveUp` does
    not guarantee stuck worker threads actually exit before the next restart
    attempt begins - a worker mid-blocking-call when the watchdog fires
    keeps running in the background (harmless: it only touches the now-
    unreferenced old `AccelContext`/buffers, eligible for GC once it
    finally returns) but can still hold a real connection slot on the same
    `ServicePoint`, competing with the new attempt's fresh workers for
    however long its own timeout takes to elapse. Not changed - `Join`
    already uses a bounded timeout deliberately (an unbounded `Join` would
    turn one already-stuck worker into a second, indefinite hang blocking
    the restart itself), and the `ConnectionLimit` fix above removes most of
    the reason this would ever matter.

## Accelerated download: two MORE confirmed regressions, found by actually
## simulating mirror failures - the ServicePointManager fix above was real
## but not sufficient on its own
Follow-up to the section above, after the user asked to actually simulate a
mirror stalling/dying mid-download and check recovery, rather than relying on
a healthy live network to (not) reproduce the incident. Built a controlled
test harness (`StallHarness.cs`, kept only in the scratch dir, not the repo):
two local `HttpListener` "fake mirrors" serving an identical in-memory 176MB
file (11 exact 16MB chunks) with a real matching SHA-256 sidecar, each with a
`Stalled`/`FullyDown` flag this harness's own scheduler thread can flip on a
timer - lets a mirror's failure be turned on/off deterministically, which the
real `updates.smce.pl`/`updates2.smce.pl` pair obviously can't be for testing.
Compiled against the real `AcceleratedDownload.cs`/`Net.cs`/`AppConfig.cs`/
`Logger.cs` every time - never a reimplementation.

**Bug 2 - a worker exits FOREVER the first time it finds the shared work
queue momentarily empty**, instead of continuing to poll it. `WorkerLoop`
did one `TryDequeueLowest` per loop iteration and `return`ed immediately if
it came back empty. Reproduced live: with mirror A stalled and mirror B
healthy, B's workers raced ahead and drained every chunk that wasn't already
checked out by A within the first ~200ms, found the queue empty, and
permanently exited - so when A's stuck chunks eventually failed and were
re-enqueued, B had nobody left to pick them up. Confirmed via added `[DIAG]`
logging (temporary, stripped before the final build) that exactly the chunks
originally assigned to the dead mirror were never serviced by anyone, in all
3 restart attempts, and `TryDownload` failed outright despite the other
mirror being 100% healthy the whole time. Diffing against DeploymentTool's
own `AccelWorkerLoop` found it has the identical scenario called out **by
name** in its own comment: *"a chunk still checked out by a worker on the
OTHER mirror can yet fail and land back in pending — if this thread exited
right now, nobody would be left to pick that chunk up."* Its fix - keep
polling (with a short sleep) until a `Remaining` counter (chunks not yet
resolved) hits zero, or the attempt is being torn down - was ported in as-is
(`AccelContext.Remaining`, `Interlocked.Decrement` at both the owner-win and
hedge-win completion points in `AcceleratedDownload.cs`).

**Bug 3 - `source.OnFailure()` was counted per fully-exhausted CHUNK (up to
`AccelMaxRetriesPerChunk`=2 retries deep) instead of per individual failed
HTTP try**, making "mark this source Dead" take roughly 3x longer than
intended AND letting a dying source's own worker win the race to re-claim
its own just-failed, re-enqueued chunk ahead of the healthy mirror's workers
(which only poll the queue every 50ms per the Bug 2 fix, while the failing
owner loops back to re-dequeue immediately, no sleep). DeploymentTool's own
comment on `AccelRunFetchSide` says this in so many words: *"ConsecutiveFailures
is incremented ... per FAILED HTTP ATTEMPT ... a truly dead mirror is
detected in roughly one timeout period this way rather than needing several
full per-chunk timeouts to accumulate."* Moved `source.OnFailure()` into
`FetchRange`'s own catch block (one call per failed try, matching
DeploymentTool exactly) and removed the now-redundant calls at the two
outer call sites (`WorkerLoop`, `RunHedge`) that would otherwise double-count.

**Bug 4 (found investigating why Bug 2+3's fix still wasn't reliably
enough on their own) - TryDownloadCore's 3 restart attempts don't remember a
source was already proven dead**, so each attempt spawns fresh workers for
BOTH sources again and re-runs the identical failure from scratch. Combined
with the strictly-in-order writer (a chunk's buffer is only released back to
the shared pool once every earlier-indexed chunk has been written), a mirror
that dies while holding a LOW chunk index can head-of-line-block the writer
long enough for the healthy mirror to fill the entire (deliberately small,
`AccelTotalConnections`-sized) buffer pool with completed-but-unwritten
chunks - confirmed this exact deadlock live once, though it turned out to be
timing-dependent (not reproducible every run) and became far less likely
once Bug 3's fix made dead-mirror detection ~3x faster. DeploymentTool
avoids the whole class of problem differently: it re-runs a cheap `HEAD` on
BOTH servers at the top of **every** restart attempt (not just once, up
front) and explicitly runs that attempt single-source (zero workers spawned
for the bad one) if the backup/mirror doesn't answer or has since diverged -
its own comment: *"backup unavailable/diverged on restart ... continuing
single-mirror on main rather than aborting the whole accelerated attempt."*
Main itself is treated as non-negotiable (if IT fails the recheck, the whole
accelerated path aborts - there's no single-source-on-mirror-alone mode,
matching the plain fallback path which only ever uses `BaseUrl`/main too).
Ported this in: `TryDownloadCore`'s retry loop now tracks `skipMirror`
across attempts (set when `RunAttempt` reports the mirror died), re-checks
the mirror's `Head()` (size/timestamp agreement with a fresh main `Head()`)
at the top of each subsequent attempt to decide whether to keep skipping it
or give it another chance, and aborts outright if MAIN fails its own
recheck. `RunAttempt` takes new `skipMain`/`skipMirror` parameters (seeds
that source's `AccelSource.Dead` true from the start via a new
`MarkDeadFromStart()`, and gives 100% of `AccelTotalConnections` to the
surviving side instead of the speed-weighted split) and reports back
`mainDied`/`mirrorDied` via `out` parameters.

**Re-verified live with all three fixes in place**, using the same
`StallHarness`, byte-for-byte content verification against the known-good
in-memory file on every success:
- Mirror A dead from start, B healthy: recovered and completed in one
  attempt, ~16.5s (previously: failed all 3 attempts, ~48s, 0 bytes usable).
- Mirror B dead from start, A healthy: same, ~16.5s, symmetric.
- Both dead for 20s then both recover: attempt 1 correctly stall-watchdogs
  at 15s, attempt 2's restart re-checks both, finds them healthy again,
  resumes dual-source, completes at ~20.6s.
- Both dead for the entire run: exhausts all 3 restart attempts (~48s) and
  returns `false` cleanly - does NOT hang. `Net.DownloadFile`'s existing
  fallback to the plain single-connection path is what the caller relies on
  from here; that path only ever uses `BaseUrl`, so it succeeds if main
  specifically is the healthy one and fails/retries/throws the same as
  before if not - unchanged, not something this round of fixes touched.
- Re-ran the original real-mirror healthy-path test (real 2.26GB ESD, real
  `updates.smce.pl`/`updates2.smce.pl`) after all three fixes to confirm no
  regression to the normal case: 156 MB/s average, hash verified, matches
  earlier runs.

**What this does and doesn't prove about the user's original incident**:
these three bugs, especially Bug 2, reliably reproduce a total, permanent
`TryDownload` failure from nothing more than ONE mirror going bad partway
through a download - a far more mundane and likely trigger than the
ServicePointManager gap's "needs marginal network conditions to matter"
profile. Still can't point at the exact one that fired on the user's real
machine (no log capture of that specific incident survived - see the log
rotation/clearing note elsewhere in this file), but Bug 2 in particular is
not conditional on network quality at all - it fires from ordinary mirror
unavailability, deterministically, every time. All four accelerated-download
bugs found in this round were fixed and re-verified - see the next section
for four MORE found in a follow-up "is anything else still missing" pass.

## Accelerated download: four MORE regressions found on a follow-up "verify
## nothing else was missed" pass - a full line-by-line diff against every
## remaining part of DeploymentTool's AcceleratedDownload.cs
User asked directly: is anything else still missing from the DeploymentTool
port? Read through the entirety of DeploymentTool's
`updater\Program.AcceleratedDownload.cs` (1973 lines - header comments,
`AccelSource`/`AccelChunkState`/`AccelPendingSet` field-by-field, the full
`TryAcceleratedDownloadCore` restart loop, `RunAcceleratedTransfer`,
`AccelMonitorLoop`, `AccelScanForHedges`, `AccelWorkerLoop`,
`AccelRunFetchSide`, `AccelResolveFailure`, `AccelTryResolve`,
`AccelWriterLoop`) side by side with Triquetra's `AcceleratedDownload.cs`,
not just the sections already touched. Confirmed several things ALREADY
matched correctly and needed no change: the checkpoint/resume-across-process-
restart mechanism (still deliberately not ported, per the file's own
long-standing note), the byte-compare-on-a-genuine-hedge-tie logic
(deliberately simplified away, also already documented), hash-pinning-once-
across-restarts, the lowest-index-first `SortedSet` pending queue (Triquetra
already had this), and the overall worker/monitor/writer thread shape.

Four real gaps remained, found by then live-testing every code path this
reading raised a question about (a local two-`HttpListener`-mirror harness
again, extended with a `ThrottleBytesPerSec` per-mirror knob to force a
*genuine* hedge race, not just a dead-mirror failover, plus larger
21-chunk/336MB test files to expose buffer-pool contention that an 11-chunk
file was too small to trigger):

- **Bug 5 - losing side of a hedge/owner race was never actually cancelled.**
  DeploymentTool's `AccelTryResolve` calls `.Abort()` on the LOSING side's
  still-in-flight `HttpWebRequest` the instant the other side wins (both
  directions - owner aborts an active hedge, hedge aborts the owner).
  Triquetra's `FetchRange` only checked `shouldAbandon()` between synchronous
  calls, so a loser already blocked inside `GetResponse()`/`Read()` just kept
  running - and, before the ServicePointManager fix, could keep occupying one
  of only 2 real connection slots for up to `AccelTimeoutMs` longer than
  necessary. Fixed: `AccelChunk` now tracks `OwnerRequest`/`HedgeRequest`
  (published via a new `onRequestStarted` callback threaded through
  `FetchRange`), and both win branches (`WorkerLoop`, `RunHedge`) call
  `.Abort()` on the loser's request. Required a matching guard in
  `FetchRange`'s catch block (`if (ctx.GiveUp || shouldAbandon()) return
  false;`, checked *before* counting anything) - without it, the abort's own
  exception would have been counted as a genuine failure against a perfectly
  healthy mirror that simply lost a race. Also added the small
  `Thread.Sleep(300 * tries)` backoff between same-side retries that
  `AccelRunFetchSide` has and Triquetra's `FetchRange` didn't.

- **Bug 6 - a genuine hedge win spliced in the WRONG bytes.** `RunHedge`
  called `FetchRange(ctx, chunk.HedgeSource, chunk.ChunkStart, ...)` - passing
  the chunk's own start as the base offset, when the hedge is only supposed
  to fetch the TAIL starting at `chunk.ChunkStart + chunk.HedgeRangeStart`.
  Every genuine hedge win re-fetched the chunk's PREFIX a second time instead
  of its actual tail, silently splicing wrong bytes into the file. Confirmed
  live: forced a real hedge win (one mirror throttled to 1 MB/s, the other at
  full loopback speed) and got a final SHA-256 mismatch every time, with the
  first differing byte landing exactly at `HedgeRangeStart` - a deterministic
  correctness bug, not a timing-dependent one, reachable on real production
  config (`hedge_budget_percent=10` is the user's live setting) any time a
  hedge actually got to prove its value. Fixed by passing
  `chunk.ChunkStart + chunk.HedgeRangeStart` instead. Re-verified: 3
  consecutive forced-hedge runs, all byte-for-byte correct afterward.

- **Bug 7 - a double buffer-release, and a bogus hedge on a chunk nobody
  owns.** Two compounding issues found fixing chunk-requeue logic (see
  DeploymentTool's `AliveAttempts` - a chunk must only be requeued once
  EVERY attempt on it, owner AND any hedge, has given up, not the instant
  either one fails):
  1. Triquetra had no such counter at all - an owner failing while its own
     hedge was still active unconditionally requeued the chunk anyway
     (checked only `Phase != 2`), letting a second worker dequeue the SAME
     index and start overwriting the reused `AccelChunk`'s `OwnerBuffer`
     while the original hedge could still be about to splice a PREFIX out of
     that same, now-concurrently-written buffer. Ported `AliveAttempts`
     in: incremented when a hedge launches, decremented on either side's
     failure, only the decrement that reaches 0 actually requeues.
  2. Getting this right exposed a second, genuinely pre-existing bug
     (present before Bug 5/6/7's other changes, just harder to reach): when
     an owner finished successfully but had already lost to a hedge
     (`else { ctx.BufferPool.Add(buffer); }` in `WorkerLoop`), it released
     its OWN buffer immediately - but the writer, once it later commits that
     hedge-won chunk, ALSO unconditionally releases `chunk.OwnerBuffer`
     (needed for the chunk's PREFIX bytes). Same buffer object added to the
     shared pool twice, meaning two unrelated concurrent fetches could end up
     sharing (and corrupting) one buffer. Fixed: the owner's own
     success-but-lost and failure paths no longer touch `OwnerBuffer` at all
     unless `AliveAttempts` proves the chunk is genuinely, fully abandoned -
     the writer (on a hedge win) or the owner's own win branch is the only
     other place it's ever released.
  3. Fixing this recycle path introduced a NEW bug of its own, caught by
     live testing, not review: recycling reset `Phase`/`AliveAttempts` but
     left `chunk.Owner`/`OwnerBuffer`/`OwnerBytesSoFar` pointing at the OLD,
     dead attempt. `AccelScanForHedges`' only gate for "does this chunk
     already have an owner" is `Phase != 0 || Owner == null` - with the
     stale `Owner` still set, it could "hedge" a chunk that was actually just
     sitting idle in `Pending`, using a stale byte-range snapshot AND a
     buffer reference that had already been returned to the pool (and could
     already be reassigned to something else entirely). That bogus hedge
     always "wins" (nothing real is racing it) and commits whatever garbage
     currently occupies the recycled buffer as the chunk's prefix. Fixed by
     also clearing `Owner`/`OwnerBuffer`/`OwnerBytesSoFar` at both recycle
     sites, so a chunk sitting in `Pending` always looks genuinely
     owner-less.

- **Bug 8 - buffer acquired AFTER dequeuing the chunk index, not before.**
  `WorkerLoop` called `Pending.TryDequeueLowest` first and only then waited
  for a free buffer from `BufferPool`. DeploymentTool's `AccelWorkerLoop`
  does the opposite: buffer first, dequeue second (its own comment: "Bounded
  wait, not bufferPool.Take()..."). The ordering matters under real
  contention: with Triquetra's order, a worker can dequeue (remove from
  `Pending`) a low chunk index and then block waiting for a buffer - during
  that wait NO OTHER worker can service that index even if one has a buffer
  free right now, since it's no longer in the pending set at all. Combined
  with the strictly-ordered writer (itself the reason buffers were scarce -
  it can't release any until the low index it's actually waiting for
  resolves), this reliably deadlocked: confirmed live, a 21-chunk file with
  one dead mirror got stuck with several chunks completed-but-unwritten
  (holding their buffers hostage) while the couple of low indices the writer
  actually needed sat dequeued-but-buffer-starved, going nowhere until the
  attempt's stall watchdog gave up - and since nothing about the pattern
  differs between restarts, it failed identically 3 times in a row (~95s
  total) before giving up for good. Fixed by swapping the order to match
  DeploymentTool exactly. Re-verified: the same 21-chunk/one-dead-mirror
  scenario that reliably failed at ~95s now reliably completes at ~17s
  (3 consecutive passes), and the four scenarios from the section above
  (A/B/BOTH_RECOVER/BOTH_PERMANENT) plus the forced-hedge scenario all still
  pass with the larger file.

**Note on how Bug 8 was found**: it was masked at the smaller 11-chunk file
size used in the section above - only showed up once the test file was
enlarged to 21 chunks while chasing a *different* problem (making a genuine
hedge race reproducible), a reminder that this class of contention bug is
inherently file-size/chunk-count sensitive and an 11-chunk repro is not
sufficient evidence that a fix actually generalizes. All eight
accelerated-download bugs found across both rounds are now fixed and
re-verified together (fresh build, full scenario matrix, plus the original
real-mirror 2.26GB ESD test) with no regressions.

## Native DISM API path showed NO progress at all - a real gap, now fixed
User-reported, live: with "Use DISM API" enabled, the status bar showed
nothing during a real install - no percentage, no movement - where the CLI
path always shows something. Root cause: `DismAddPackage`/`_DismCleanImage`'s
own `DISM_PROGRESS_CALLBACK` (wired correctly in `DismNative.cs` since it was
first built) goes just as silent as `dism.exe`'s own stdout percentage does
for long stretches of a large ESD/servicing-stack install - same underlying
CBS engine either way, this isn't specific to the API path. `AddPackageCli`
already had a heartbeat thread specifically for this (its own comment: "ESD
packages: DISM emits nothing for many minutes while expanding payload
internally"), but `TryAddPackageNative`/`TryResetBaseNative`
(`DismHelper.cs`) never got the equivalent when they were written - the
native call just blocked with whatever status text was already on screen
before it started. Fixed by extracting the CLI path's heartbeat into a
shared `DismHelper.StartHeartbeat(progressCb, stop)` and calling it from all
three call sites (`AddPackageCli`, `TryAddPackageNative`,
`TryResetBaseNative`) - any real progress DISM does send through (native
callback or parsed stdout) still overwrites the heartbeat's "working ... Ns"
text in between ticks, same as it already did on the CLI side.
- **What was verified vs. not**: a standalone harness (`DismHelper.cs` +
  `DismNative.cs` + `AppConfig.cs` + `Logger.cs` + `VersionCompare.cs`, same
  pattern as this project's other DISM/download harnesses) confirmed the
  heartbeat starts and stops cleanly around a native `DismAddPackage` call
  with `UseDismApi=true` (a bogus 60MB file, made deliberately large so
  DISM's own rejection wouldn't be instantaneous) - no hang, no lingering
  thread activity 3s after return. What this did NOT prove: DISM actually
  rejected the fake file in ~0.26s (validates the CAB header fast, doesn't
  need to read far into a malformed file to reject it), too fast for even
  one 1000ms heartbeat tick to fire - so a real mid-flight "working ... Ns"
  tick during an actual multi-minute ESD install was not observed live, only
  reasoned about (identical code structure to the CLI heartbeat, which has
  been proven in production). Confirming an actual tick firing requires a
  real install, which this project's sessions have consistently avoided
  triggering.

## BaseUrl/MirrorUrl normalize themselves - never TrimEnd('/') at a call site
`AppConfig.BaseUrl`/`MirrorUrl` are properties (not plain fields) whose setter
normalizes to always end in exactly one `/` (or stay `""` if unset) -
`AppConfig.cs`. Before this, half a dozen call sites (`EnablementPackage.cs`,
`MainForm.cs`'s self-update/`FetchBuildsInner`/`InstallWorker`,
`AcceleratedDownload.cs`) each independently did their own
`.TrimEnd('/') + "/"`, and the user still had to type a trailing slash for it
to look right. Normalizing once, on assignment (covers `Load()` from
`triquetra.conf`, the Settings tab, and any future call site) means every
consumer can just concatenate a relative path directly - confirmed live that
typing `https://updates.smce.pl` (no slash) into the Settings tab now fetches
the build list correctly, and the Settings tab's own textbox displays it with
the trailing slash already added (reads the same normalized property at
construction, no separate reformatting step needed).

## Verification method (no native GUI automation tool in this environment)
Built via `build.cmd`, launched with `Start-Process`, screenshotted with `user32.dll` `GetWindowRect` + `Graphics.CopyFromScreen` (matches the window's full outer bounds - `Form.Size`/`GetWindowRect` agree since `SetProcessDPIAware` is called, no coordinate-scaling surprises), clicks simulated via `SetCursorPos`+`mouse_event`. Pixel-verified with `Bitmap.GetPixel` at specific coordinates AND full horizontal scanlines (printing every color transition) rather than trusting a single spot-check or eyeballing the inline preview - a single bad sample point can land on anti-aliased text, the OS caption-bar/DWM-Mica tint region (a few px below y=0 still isn't "client area"), or a drop-shadow/rounded-corner edge a few px from the window's outer bottom/side, any of which will read as a plausible-but-wrong color and can manufacture a false bug (or hide a real one) if not cross-checked with a wider scan.
