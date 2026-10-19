# DetailFrame installer and release layout

## Staging layout (all generated, git-ignored under `artifacts/`)

| Path | Producer |
|---|---|
| `artifacts/app/win-x64/` | `dotnet publish` of `src/DetailFrame.App` (unpackaged, x64, self-contained) |
| `artifacts/worker/win-x64/` | `worker/packaging/build.ps1` (PyInstaller) |
| `artifacts/installer/DetailFrame-Setup.exe` | installer compiler (a later task) |
| `artifacts/installer/release-manifest.json` | `installer/verify-artifacts.ps1` |

Installer inputs are limited to the two artifact directories plus versioned files in `installer/` and `src/DetailFrame.App/Assets/Branding`. Build outputs are never committed.

## Scripts

- `pwsh installer/build.ps1 [-SkipWorker] [-SkipApp]` builds the worker and app, then verifies.
- `pwsh installer/verify-artifacts.ps1 [-RequireInstaller]` fails if an input is missing, a binary is not x64, the staging roots, their parents or any file is a reparse point or resolves outside the staging directory, the built `detailframe-worker.exe --version` disagrees with `worker/pyproject.toml`, or the artifact root is outside `<repo>/artifacts`. It writes a deterministic (timestamp-free) manifest with app version, worker version, architecture, worker package inventory, a comprehensive app runtime inventory (every published file that is not app-owned, with version and SHA-256) and a SHA-256 plus `app`/`runtime`/`worker` role for every file. The worker version is read from the built executable.
- `pwsh tests/release/test_release_layout.ps1` checks the layout rules and verifier behaviour against fixtures.

## Installer tool decision: Inno Setup 6

Evidence gathered 2026-10-20:

- Available: `winget search "Inno Setup"` lists `JRSoftware.InnoSetup` 6.7.3 and `JRSoftware.InnoSetup.7` 7.1.0; jrsoftware.org/isdl.php lists 6.x and 7.x downloads and source.
- Maintenance: active; 6.6 and 6.7 shipped dark mode, custom wizard images and RedirectionGuard support.
- License: free to use and distribute installers; the vendor asks commercial users to purchase a license (isorder.php). Review before commercial release.
- Fit: a single `.iss` script compiled by `ISCC.exe` produces one per-user/per-machine `DetailFrame-Setup.exe` from plain directories, which suits an unpackaged app plus a PyInstaller folder. Comparisons with WiX and NSIS were not researched or verified and are not part of the justification for this choice.

Inno Setup 6 is the stable line; 7.x is acceptable if the later installer task prefers it. The compiler is not required for Task 1.

## Installer script and prerequisites (Task 3)

Files: `installer/DetailFrame.iss` (Inno Setup script), `installer/prerequisites.ps1` (prerequisite logic), `tests/release/test_installer_prerequisites.ps1`.

**Tested Inno Setup version: none.** ISCC is not installed on the authoring machine, so `DetailFrame.iss` has never been compiled; only static checks run. It uses features from Inno Setup 6.3+ (`x64compatible`); compile it with 6.7.3 (or 7.x) and fix any compiler findings before release.

Build tools: .NET SDK (see `build-app.ps1`), the worker build tools (`worker/packaging/build.ps1`), PowerShell 7, Inno Setup 6.3+.

Compile (after `installer/build.ps1` has staged artifacts and the runtime bootstrapper has been fetched):

```powershell
pwsh installer/prerequisites.ps1 -Mode Download -DestinationDir artifacts/installer/prereq
ISCC.exe /DAppVersion=1.0.0 installer\DetailFrame.iss   # -> artifacts\installer\DetailFrame-Setup.exe
```

Install behaviour: per-machine (admin), x64 Windows 11 only, default `{autopf}\DetailFrame`. App goes to the install dir and the CPU worker to `{app}\worker`; no GPU libraries are packaged. Start Menu (default on) and desktop (default off) shortcuts are optional tasks using the DetailFrame icon. Settings and model weights are never written under Program Files; uninstall leaves `%LOCALAPPDATA%\DetailFrame` and the shared Windows App SDK runtime in place. The app locates the worker at `{app}\worker` by this installer's convention; confirm this matches the app's lookup when the shell integrates the worker.

Windows App SDK: the app is unpackaged and framework-dependent on Windows App SDK 2.5.1 (`WindowsAppSDKSelfContained` is not set), so the runtime must be present. The installer checks `Microsoft.WindowsAppRuntime.2` >= 2.5.1.0 and, if missing, runs the bundled official bootstrapper `WindowsAppRuntimeInstall-x64.exe --quiet` (provisioning needs the installer's elevation), then re-checks. Failure is reported explicitly and aborts setup.

Bootstrapper evidence (2026-10-20): `https://aka.ms/windowsappsdk/2.5/2.5.1/windowsappruntimeinstall-x64.exe` redirects to `download.microsoft.com`; downloaded 120,199,480 bytes, SHA-256 `931a421e8dc3e6e67724806cb67fecdbb88dfe323f0170842eb4a4b4b149f1e2`, valid Microsoft Corporation Authenticode signature, reports version 2.5.1. The hash is pinned in `prerequisites.ps1`; both the download helper and the installer-time check refuse a mismatch. It adds about 120 MB (before compression) to the installer. Not yet verified: actual install on a machine lacking the runtime, and compile/size of the installer.

Alternatives: if the single-`.exe` bootstrapper proves unreliable, compare MSIX packaging (runtime resolved via package dependency, but different uninstall/sideload story) before replacing Inno Setup. No change was made.

Exit codes of `prerequisites.ps1`: 0 ok, 10 unsupported OS, 11 unsupported architecture, 12 runtime install failed, 20 runtime missing (`-ProbeOnly`).
