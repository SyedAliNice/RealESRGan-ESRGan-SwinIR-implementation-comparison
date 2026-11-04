# DetailFrame Windows App Design

- **Status:** Approved architectural direction; implementation details remain subject to the validation gates in this spec.
- **Date:** 2026-10-19
- **Target:** Windows 11 desktop application for single-image super-resolution.

## Summary

Build DetailFrame as a native WinUI 3 application that guides a user from choosing one image through model selection, enhancement, before/after inspection, and saving the result. Run inference locally through a bundled Python/PyTorch CPU runtime, with optional NVIDIA CUDA acceleration only where the selected model and installed runtime are verified compatible. Download model weights on demand from verified official sources; expose only model/scale combinations with a validated inference path and verified weights.

The current repository is a notebook-based model comparison, not an application. Its README names Real-ESRGAN, ESRGAN, and SwinIR; notebook cells demonstrate some model setup and inference code. The repository does not establish a production-ready Windows inference path or a verified inventory of compatible weights. Therefore, those model names are candidates for evaluation, not a promise that any particular model will be enabled at launch.

## Goals

- Provide a polished, native-feeling Windows 11 workflow for enhancing one image at a time.
- Accept PNG, JPEG, and WebP through a file picker or drag-and-drop.
- Let users choose among validated models, supported scales, and available processing devices, with sensible defaults.
- Show an interactive before/after wipe comparison with zoom.
- Save the enhanced image to a user-selected location.
- Keep image processing local. Network access is limited to explicit model-weight downloads.
- Ship an installer `.exe` that includes the application and CPU inference runtime; make CUDA support optional.
- Use the supplied DetailFrame branding for the application and installer icon.

## Non-goals

- Batch processing, video, training, model fine-tuning, or cloud inference.
- Enabling a model merely because its name appears in a notebook or README.
- Guaranteeing CUDA support for every NVIDIA GPU, driver, CUDA toolkit, or model.
- Bundling unverified third-party or repository weights as a substitute for validating their provenance and compatibility.
- Supporting platforms other than Windows 11.

## User experience

### Guided workflow

Present the main workflow as a persistent sidebar with clear stages:

1. **Choose image** — open one PNG, JPEG, or WebP file, or drop one into the application.
2. **Configure** — choose an enabled model, one of its supported scales, and a processing device.
3. **Enhance** — start inference, show progress, and allow cancellation where the engine supports safe cancellation.
4. **Compare** — inspect the original and result using a draggable before/after divider and zoom.
5. **Save** — use Save As to choose the result's destination and file type.

Stages should communicate availability and completion without allowing a user to start inference before an image, valid model, and device are ready. Keep the result available for comparison until the user chooses another input or starts a new job.

### Defaults and controls

- Follow the current Windows light/dark theme and system accent where practical.
- Default device is **Auto**: use CUDA only when runtime compatibility is positively established for the selected model; otherwise use CPU. Show the chosen device and make CPU selectable whenever supported.
- Select a default model and scale only from the enabled registry. If no verified model is available, explain that enhancement is unavailable rather than offering a broken or speculative choice.
- Model and scale controls must update together so unsupported combinations cannot be selected.
- Show model download size when known, download progress, and whether the selected weights are already installed.

### Image handling and comparison

- Validate the actual decoded image and format, not only its filename extension. Reject unsupported, corrupt, or disallowed input with an actionable error.
- Keep the source image unchanged. Treat inference output as a separate result and do not overwrite the source.
- The comparison view presents original and enhanced output at a shared zoom and pan position. The wipe handle must be keyboard accessible; zoom and fit-to-view controls must remain available.
- Display progress and useful failure information for decode, model download, model load, inference, and save operations.
- The user chooses the destination and output format through Save As. Encoding options and default format should be finalized during implementation with PNG as the fidelity-preserving baseline.

## System architecture

### Desktop shell

Use **C# with WinUI 3 and Windows App SDK** for the user interface and Windows integration. The desktop shell owns:

- Windowing, theme, navigation, accessible controls, and workflow state.
- File picker, drag-and-drop, supported-format validation, and Save As.
- Device capability discovery and presentation of compatible device options.
- Model catalog and download UI, including provenance and integrity checks.
- Job lifecycle, cancellation requests, progress display, and user-facing errors.
- The original/result comparison surface.

Keep application state explicit: selected input, selected compatible model/scale/device, model download state, active job, result availability, and save state. A failed or cancelled job must not be presented as successful or replace a previously completed result.

### Inference worker

Run model inference in a separate, bundled Python worker using the CPU PyTorch runtime. Process isolation keeps the UI responsive and prevents Python/native-library failures from taking down the shell. The worker owns model construction, checkpoint loading, image tensor preparation, inference, and result generation.

Use a versioned JSON Lines protocol over the worker's standard input/output for commands and events. The protocol should cover at least:

- Worker capability and version handshake.
- Model/scale/device validation before a job starts.
- Enhancement job start, progress, completion, structured failure, and cancellation request.
- Terminal cancelled state, including safe worker termination when an engine cannot stop cooperatively.

Reserve standard output for protocol messages; write diagnostic logs to standard error and rotating local log files. Include a job identifier in events so stale messages cannot update a newer job. Treat malformed messages, worker crashes, and protocol-version mismatches as explicit errors. The shell should terminate a stuck worker after a bounded shutdown period and preserve actionable diagnostics without exposing paths or data unnecessarily.

Pass input and output through controlled temporary files or another explicitly bounded local IPC mechanism; do not serialize full-resolution image pixels as JSON. Use per-job temporary storage, clean it after completion/cancellation, and never overwrite user input. If cooperative cancellation is unavailable, terminate the isolated worker and report cancellation only after it exits; discard partial output and retain any prior completed result. The implementation must define maximum image dimensions/memory behavior and test large-image handling before release.

### Model registry and weight lifecycle

Maintain a data-driven registry of model variants. A registry entry is eligible to appear as enabled only when all required evidence is present:

- A supported architecture implementation and documented license.
- A verified official weight source and immutable expected SHA-256 digest obtained through the release/validation process.
- A compatible checkpoint format and architecture configuration.
- Explicit supported scale(s), input/output behavior, and device requirements.
- Successful CPU inference validation and, separately, CUDA validation for any advertised CUDA path.
- Recorded provenance, weight version, and validation results.

Do not infer compatibility from a filename, README statement, notebook output, model name, or successful download alone. HTTPS transport is necessary but is not by itself proof that a checkpoint is official or compatible. If source authenticity, checksum, architecture, or inference validation is missing, keep the entry disabled and state why.

Download weights only when requested or needed for the selected enabled model. Download into a temporary file under the current user's local application data, verify the expected digest and required metadata, and atomically move the file into the model cache only after every check succeeds. An incomplete, corrupt, mismatched, or unverified download must never become loadable. Store installed weights in a documented per-user cache (for example, `%LOCALAPPDATA%\\DetailFrame\\Models`) and provide a way to retry failed downloads; deletion/management UI can be scoped to a later release.

The existing notebooks reference model sources and demonstrate selected Real-ESRGAN/SwinIR code paths; they do not prove that the referenced checkpoints are currently verified, compatible with a packaged Windows worker, or licensed for redistribution. The current `models.zip` contains a SwinIR Python source file and bytecode, not a verified weight catalog. Initial launch support must be determined by the validation process rather than assumed from the repository's model list.

### Device selection

CPU inference is the baseline and must work without CUDA, an NVIDIA GPU, or a network connection after required weights are installed. CUDA is an optional acceleration path:

- Detect actual runtime/model compatibility, not merely the presence of an NVIDIA GPU.
- Make CUDA selectable only when the packaged or otherwise supported runtime can load the required native components and execute the selected model.
- Fall back to CPU only when the user chose Auto and the fallback is safe; show the effective device. If the user explicitly selected CUDA and it becomes unavailable, report the reason and ask them to select CPU or Auto rather than silently changing the request.
- Keep CUDA components optional in distribution and document driver/runtime prerequisites before enabling that install option.

## Installation and updates

Deliver a Windows installer `.exe` containing the WinUI application and a self-contained CPU Python/PyTorch inference runtime so users do not need to install Python. Do not bundle CUDA libraries in the baseline installer. If CUDA is offered, package or install its required runtime components through an explicit optional path with a documented compatibility policy; do not rely on an arbitrary system Python environment.

Installer packaging must register the DetailFrame application icon and support normal Windows install/uninstall behavior. Keep user model weights and settings outside the application install directory so application updates do not overwrite them. Installer signing, update delivery, and minimum hardware/storage requirements must be decided before public distribution; this spec does not assume those services or policies already exist.

## Branding assets

Use the supplied ICO as the application and installer icon, and use the supplied SVG assets for scalable in-app branding as appropriate. The supplied paths were accessible during spec preparation:

- `C:\\Users\\cihan\\.copilot\\attachments\\f8ee6391-7e86-4075-93be-7131372e5bd0-app.ico`
- `C:\\Users\\cihan\\.copilot\\attachments\\03c3ee41-05af-4d65-af49-c81b6519c072-detailframe.svg`
- `C:\\Users\\cihan\\.copilot\\attachments\\d41e8509-ee39-45af-92c4-7adbb5d0ade4-detailframe-small.svg`
- PNG export set: `C:\\Users\\cihan\\OneDrive\\AI Projects\\workspace\\DetailFrame\\assets\\exports`

These are local user assets, not repository files. Before implementation or packaging, copy the selected production assets into an intentional, version-controlled application asset location (or otherwise make them part of a reproducible build input); do not make builds depend on these machine-specific paths.

## Privacy, security, and reliability

- Perform image decoding and inference locally; do not upload image content.
- Limit network access to explicit model-weight downloads.
- Use HTTPS, pinned expected hashes, strict checkpoint validation, and atomic cache installation for weights.
- Keep user data out of logs. Diagnostics should identify the failing stage and include safe technical details.
- Treat input images and downloaded checkpoints as untrusted data. Validate formats and handle malformed files without crashing the application.
- Handle disk-full, permission, network interruption, corrupt checkpoint, unsupported device, worker crash, cancellation, and save failure as distinct visible states.
- Preserve source and completed output when a later operation fails.
- Provide keyboard navigation, accessible names, and sufficient contrast for the primary workflow and comparison controls.

## Acceptance criteria

1. The application installs and launches on a supported Windows 11 system without requiring a separately installed Python runtime.
2. Users can load one valid PNG, JPEG, or WebP through both the picker and drag-and-drop; invalid or unsupported images receive actionable errors.
3. Only registry entries with verified source, checksum, architecture/scale mapping, and passing inference validation can be selected.
4. A supported model can be downloaded on demand, verified before installation, and reused from the local cache. Failed or modified downloads are rejected and cannot be loaded.
5. Every enabled model has a passing CPU inference path. CUDA is offered only for combinations that pass compatibility validation; Auto selects a verified available path and clearly shows the effective device.
6. Inference does not freeze the WinUI interface. Progress, cancellation, worker failure, and completion are communicated as distinct states.
7. The completed result can be inspected with a draggable before/after wipe and zoom, then saved to a user-selected destination without altering the source.
8. The interface follows Windows light/dark theme changes and the main controls are keyboard accessible.
9. The baseline installer includes the application and CPU runtime; CUDA remains optional and its prerequisites are explained.
10. Builds use repository-managed branding assets rather than machine-specific user paths.

## Repository implications and open validation

The repository currently contains Jupyter notebooks, sample images, a README, and `models.zip`; it has no WinUI project, installer, application-level test suite, or verified model catalog. The README's suggested `enhance.py` invocation is not present as a top-level implementation. The notebooks use Colab-oriented setup and external repositories, so they are research evidence rather than a Windows packaging contract.

Before enabling any model, implementation work must identify the exact official weight artifact and license, pin and verify its digest, establish a supported Windows inference path, and test the model/scale/device combinations on CPU and any advertised CUDA runtime. Do not claim a model or weight is verified until those checks have actually passed.

The architecture intentionally separates the Windows shell from inference so the interface, model lifecycle, and packaging can be developed and tested independently. The first implementation should validate one candidate end to end before expanding the enabled catalog.
