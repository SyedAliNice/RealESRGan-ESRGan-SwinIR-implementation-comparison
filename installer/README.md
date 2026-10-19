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
- `pwsh installer/verify-artifacts.ps1 [-RequireInstaller]` fails if an input is missing, a binary is not x64, any path is a reparse point or resolves outside the staging directory, or the artifact root is outside `<repo>/artifacts`. It writes a deterministic (timestamp-free) manifest with app version, worker version, architecture, worker package inventory, app runtime inventory and SHA-256 of every file.
- `pwsh tests/release/test_release_layout.ps1` checks the layout rules and verifier behaviour against fixtures.

## Installer tool decision: Inno Setup 6

Evidence gathered 2026-10-20:

- Available: `winget search "Inno Setup"` lists `JRSoftware.InnoSetup` 6.7.3 and `JRSoftware.InnoSetup.7` 7.1.0; jrsoftware.org/isdl.php lists 6.x and 7.x downloads and source.
- Maintenance: active; 6.6 and 6.7 shipped dark mode, custom wizard images and RedirectionGuard support.
- License: free to use and distribute installers; the vendor asks commercial users to purchase a license (isorder.php). Review before commercial release.
- Fit: a single `.iss` script compiled by `ISCC.exe` produces one per-user/per-machine `DetailFrame-Setup.exe` from plain directories, which suits an unpackaged app plus a PyInstaller folder. Alternatives (WiX 5+ carries an Open Source Maintenance Fee for some use; NSIS has less active maintenance) were not chosen.

Inno Setup 6 is the stable line; 7.x is acceptable if the later installer task prefers it. The compiler is not required for Task 1.
