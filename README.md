# Triquetra

Triquetra is a Windows 11 update tool. It downloads CAB/ESD/MSU update
packages from a self-hosted HTTP mirror and installs them via DISM, matching
the update to the local machine's build branch automatically.

It is a C# 5 / .NET Framework 4.8 WinForms application, compiled to a single
`triquetra.exe` with no external UI framework and no bundled resources or
DLLs.

> **This tool ships with no default server.** It is intended to be pointed at
> a mirror populated by
> [UUPDumpFetcher](https://github.com/inteliboy/UUPDUMPFetcher) and served
> through an [h5ai](https://larsjung.de/h5ai/)-based (or any plain
> Apache/nginx-style directory-listing) web server. Nothing will work — not
> refreshing builds, not installing, not self-update — until you configure a
> real Base URL on the Settings tab.

## What it does

- Reads the local machine's Windows build number and branch from the
  registry and matches it against the builds published on your mirror.
- Downloads the matching CAB/ESD/MSU package(s) (falling back to a baseline
  build first if required) and verifies them against a published
  SHA-256/MD5 sidecar before installing.
- Installs via DISM — either shelling out to `dism.exe`/`powershell.exe`, or
  (optional, experimental) directly through `DismApi.dll` via P/Invoke, with
  automatic fallback to the CLI path on any native failure.
- Offers a DISM **Enablement Package** install (a small CAB that flips the
  `DisplayVersion` feature-update flag) when a full ESD isn't needed for
  version bump.
- Offers a **DISM `/ResetBase`** cleanup helper and a **Reboot now** helper.
- Self-updates `triquetra.exe` from the same mirror on startup (can be
  disabled in Settings).
- Optional **accelerated download**: splits a large file into 16MB chunks
  pulled in parallel over two mirrors, hedging a chunk onto the second
  mirror if the first looks meaningfully slower. Falls back to a plain
  single-connection download automatically whenever the accelerated path
  isn't eligible or fails (missing second mirror, disagreeing file
  identity, exhausted retries) — it is never the reason a download fails
  outright, only the reason it's sometimes faster.
- Dark/light theme, following the OS setting by default with a manual
  override that persists.

Triquetra runs elevated (UAC `requireAdministrator` manifest embedded at
build time, with a runtime elevation-prompt fallback for a build that
somehow lacks the manifest) — DISM operations and reboot require it.

## Expected server layout

Triquetra expects the configured Base URL to serve a plain HTML directory
listing (the kind h5ai, or a stock Apache/nginx autoindex, produces) shaped
like this:

```
<BaseUrl>/
├── <build>.<revision>/              e.g. 26100.9278/
│   ├── non_complete                 (optional) marks an in-progress upload; skipped
│   └── amd64/                       (or arm64/)
│       ├── SSU-<build>-x64.cab
│       ├── SSU-<build>-x64.cab.sha256
│       ├── Windows11.0-<build>-x64.esd
│       ├── Windows11.0-<build>-x64.esd.sha256
│       └── ...
├── EP/
│   └── amd64/                       (or arm64/)
│       └── Windows11.0-KB....cab    Enablement Package cabs
├── speed.test                       (only needed for accelerated downloads)
├── triquetra.exe                    (optional) self-update target
├── triquetra.exe.sha256
└── ...
```

This is exactly the layout
[UUPDumpFetcher](https://github.com/inteliboy/UUPDUMPFetcher) produces when
it fetches and stages a build, so the normal workflow is: run UUPDumpFetcher
against a build, host the resulting folder tree behind h5ai (or any server
that emits plain `<a href>` directory listings), and point Triquetra's Base
URL at it.

A second, byte-identical mirror can be configured for the accelerated
downloader; it isn't required for anything else.

## Configuration (Settings tab)

| Setting | Notes |
|---|---|
| Base URL | Required. No default is baked in. |
| Username / Password | Sent as HTTP Basic auth if your mirror requires it. |
| Disable self-update | Skips comparing/replacing `triquetra.exe` against the mirror's copy on startup. |
| Use DISM API | Experimental: calls `DismApi.dll` directly instead of shelling out to `dism.exe`. Falls back to the CLI path automatically on any native failure. |
| Enable accelerated download | Turns on the hedged multi-mirror downloader. |
| Mirror server | Second mirror used only when accelerated download is enabled. |
| Hedge budget % | Caps how much redundant (hedged) traffic the accelerated downloader is allowed to spend. `0` disables hedging while keeping multi-connection splitting. |

All settings are stored in `%ProgramData%\triquetra\triquetra.conf`.

## Building

Requires the .NET Framework 4.8 Developer Pack (for `csc.exe`) on Windows.

```
build.cmd
```

This locates `csc.exe` under `Framework64` (falling back to the 32-bit
Framework directory), compiles every file under `src\*.cs` into
`triquetra.exe`, embeds the `requireAdministrator` UAC manifest, bumps the
build's revision number in `version.txt`, and — best-effort — signs the
result with `signtool.exe` and writes `.md5`/`.sha256` checksums alongside
it. Signing is skipped with a warning if `signtool.exe` isn't available.

## Usage

1. Launch `triquetra.exe` (you'll get a UAC prompt).
2. Go to the **Settings** tab and set your Base URL (and credentials, if
   your mirror needs them).
3. Go to the **Update** tab and click **Refresh builds**.
4. Pick a build, or leave the selection empty to install the latest
   available build for your branch.
5. Click **Install update**. Reboot when prompted if required.

## Security notes

- Triquetra runs elevated and will install whatever packages your
  configured mirror serves and self-update to whatever `triquetra.exe` it
  finds there — only point it at a mirror you control and trust.
- TLS certificate validation is currently disabled process-wide (accepts
  any server certificate) to make self-signed/internal mirrors work without
  extra setup. Don't expose your mirror to an untrusted network without
  understanding this tradeoff.
- No telemetry, no third-party services — the only network traffic is to
  whatever Base URL / Mirror server you configure.

## License

No license has been declared for this repository yet. Contact the author
before reusing this code.
