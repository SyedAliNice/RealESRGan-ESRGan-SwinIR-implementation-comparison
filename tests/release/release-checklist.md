# DetailFrame release validation checklist

Use one copy of this checklist per release candidate. Run the interactive checks on a clean Windows 11 x64 machine or VM that does not have Python or CUDA installed. Store the completed checklist and referenced evidence in a single release evidence directory, as described in [`installer/README.md`](../../installer/README.md).

**Record status honestly:** `PASS`, `FAIL`, `BLOCKED`, `N/A` (include a reason), or `NOT RUN`. A static test, staged artifact, or reviewer expectation is not evidence that an installer compiled or installed. Attach a dated evidence reference to every `PASS` and `FAIL`.

## Release candidate record

| Field | Value / evidence reference |
|---|---|
| Release version / candidate ID | |
| Source commit | |
| Checklist owner and date (UTC) | |
| Build host OS/build, architecture | |
| PowerShell, .NET SDK, Python/uv, PyInstaller, Inno Setup versions | |
| Target Windows 11 edition, OS build, architecture | |
| Test machine CPU, RAM, free disk, GPU/driver (if applicable) | |
| App version | |
| Worker version | |
| Model catalog version (or `none`; include catalog commit/hash if no version field exists) | |
| Enabled model IDs and evidence-manifest references (or `none`) | |
| Installer path, byte size, SHA-256 | |
| Artifact manifest path and SHA-256 | |
| Evidence package directory/archive path and SHA-256 | |

## Evidence package contents

Create a directory such as `release-evidence/<version>-<commit>/` and include:

- This completed checklist and a concise index of evidence files.
- Clean-checkout identity (`git rev-parse HEAD`, branch/tag, and clean/dirty status), build command/output, tool versions, and deterministic `release-manifest.json`.
- Final installer, SHA-256 output, and Authenticode signing status/result (unsigned must be stated explicitly; see policy decisions).
- Clean-machine OS/build and hardware record, install/uninstall/upgrade observations, screenshots, and sanitized application/worker logs.
- Exact model catalog, catalog hash/version, and for every enabled model its source/provenance, license, immutable expected SHA-256, architecture/scale mapping, and passing CPU validation manifest. Include CUDA evidence only if CUDA is offered.
- Results and evidence references for failure recovery, accessibility, and known limitations.
- Decisions and owners/dates for installer signing, update delivery, and minimum hardware/storage requirements.

Do not include user images, credentials, personal access tokens, or unredacted user-profile/worktree paths. Prefer synthetic test images and sanitize logs before archiving.

## 1. Reproducible clean-checkout build

- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Start from a clean checkout at the recorded source commit. Confirm no user-specific assets, generated files, or untracked inputs are required.
- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Run `pwsh -File installer/build.ps1 -Configuration Release`.
- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Confirm the command succeeds and produces exactly one installer `.exe` at the documented output path, the deterministic input inventory, and the SHA-256 artifact manifest. Record the actual compiler version and installer byte size/hash. A missing `ISCC.exe`, failed compile, or absent `.exe` is `BLOCKED`/`FAIL`, not a pass.
- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Run `pwsh -File installer/verify-artifacts.ps1 -RequireInstaller` and `pwsh -File installer/verify-no-machine-paths.ps1 -Root artifacts\app\win-x64` plus the equivalent worker payload scan. Confirm no unverified model weights or machine-specific paths are shipped.

## 2. Clean Windows 11 install and CPU workflow

Use a clean Windows 11 x64 VM or machine with no separately installed Python, CUDA toolkit/runtime, or NVIDIA driver. Record OS build and hardware in the candidate record.

- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Install the generated `.exe`; verify prerequisite handling, app registration/icon, launch, and normal install location. Record whether Windows App Runtime was already present or the bundled bootstrapper installed it. Check that setup failure is actionable if the prerequisite cannot be installed.
- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Confirm the app launches and basic navigation works without system Python, CUDA, or an NVIDIA GPU. Confirm the worker handshake/version and that the effective device is CPU when a validated CPU model is available.
- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Confirm PNG, JPEG, and WebP input picker behavior; drag-and-drop; decoded-content validation; and actionable handling of unsupported/corrupt input. Record any WebP codec dependency or missing-codec behavior.
- [ ] Status: ___  Date/tester: ___  Evidence: ___
- With at least one independently verified and enabled model, download from its documented official source, verify the expected SHA-256, install/cache it, run CPU enhancement, reopen/use the cached weight, inspect the before/after comparison, and save via Save As. Verify the input remains unchanged.
- [ ] Status: ___  Date/tester: ___  Evidence: ___
- If there is no model with complete provenance, licensing, digest, architecture/scale, and passing CPU evidence, record this workflow `BLOCKED` and block public distribution. Do not fabricate a model, weight URL/hash, successful enhancement, or cache result to fill this checklist.

## 3. Failure, recovery, and data preservation

For each scenario, record steps, expected user-visible state, actual result, and evidence. Confirm the source image and any previously completed result remain intact where applicable.

| Scenario | Status | Evidence / observed recovery |
|---|---|---|
| No network during model download | | |
| Interrupted model download; partial file is not loadable and retry works | | |
| Corrupt image and unsupported image/model input | | |
| Worker crash or malformed/failed worker response | | |
| Cancellation during an active job; no partial result is presented as complete | | |
| Disk-full or permission failure during Save As | | |
| Upgrade over a prior version; settings and model cache retained | | |
| Default uninstall preserves user data; explicit opt-in removal behaves as documented | | |

## 4. Accessibility and known limitations

- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Keyboard-only traversal and operation of primary workflow, dialogs, zoom/fit controls, and comparison wipe handle.
- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Narrator/accessibility names, focus order, visible focus, contrast, and light/dark theme behavior.
- [ ] Status: ___  Date/tester: ___  Evidence: ___
- Record known limitations, including supported formats/codecs, model/scale/device availability, maximum image/resource limits, and any scenarios not tested.

Known limitations for this candidate:

- (Record limitations or write “None”.)

## 5. Optional CUDA evidence

CUDA is not offered in the current CPU baseline. Mark this section `N/A — CUDA not offered` unless the candidate actually exposes CUDA to users. If offered, attach the exact packaged/runtime versions, supported GPU/driver matrix, and a passing CUDA inference validation record for every advertised model/scale. Verify explicit CUDA failure is clear and never silently falls back to CPU. Never claim CUDA support from GPU detection alone.

Status/reason: ___  Evidence: ___

## 6. Required public-distribution decisions and sign-off

These decisions must be recorded before public distribution; blanks or unresolved decisions block release.

| Decision | Recorded policy / owner / date / evidence |
|---|---|
| Installer signing: signed/unsigned policy, certificate custody, verification | |
| Update delivery: mechanism, ownership, security/support policy (or no updates) | |
| Minimum hardware: CPU/architecture, RAM, GPU policy | |
| Minimum free storage: install, runtime, model cache, temporary working space | |
| Model catalog: enabled entries and per-model evidence, or explicit release block | |
| Release limitations and support expectations | |

Release decision: `GO` / `NO-GO` / `BLOCKED` (select one): ___

Release approver, date, and evidence reference: ___

**GO is permitted only when** build, installer verification, clean Windows 11 install, and required CPU workflow are PASS; every published model has complete evidence; all failures are actionable and do not lose user data; required policy decisions are recorded; and any optional capability (including CUDA) passes its separate gates. If no usable verified model is enabled, the public image-enhancement release is NO-GO.
