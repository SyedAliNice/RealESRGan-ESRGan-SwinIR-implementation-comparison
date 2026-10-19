# Task 4 report

Status: DONE (with concerns below).

## Implemented
- `Models/ModelCatalog.cs`: ModelVariant, InstalledModel, DownloadProgress, errors, `ModelSelector` (model/scale/device coupling, Auto/CUDA resolution).
- `Services/ModelCatalogService.cs`: validates catalog in code (no schema-validator package), mirroring `shared/models.schema.json`. Malformed, disabled, or evidence-incomplete (no `validation_manifest`, no `cpu`) entries are dropped; malformed catalog document throws `CatalogException`.
- `Services/ModelDownloadService.cs`: HTTPS only, streams to `.partial` temp, SHA-256 + length checks, atomic `File.Move` into `%LOCALAPPDATA%\DetailFrame\Models\<sha256>\<file>`, 3 attempts for network/size failures (hash mismatch not retried), progress, cancellation, temp cleanup, safe messages (no URLs/paths).
- `Views/ConfigurePage`: empty state "No models available yet" (Enhance stays disabled), model/scale/device selectors, size display, download/cancel/retry, explicit-CUDA blocking message (no silent CPU fallback). Wired into MainWindow; `shared/models*.json` copied to output.
- No new packages.

## Deferred item: `(?i:...)`
.NET `Regex` supports inline `(?i:...)` groups; a test loads the schema, asserts the code constants equal the schema patterns, and exercises the URL pattern (incl. `HTTPS://`). Chosen approach: in-code validation, no validator dependency.

## Tests
`dotnet test DetailFrame.sln`: Passed 57, Failed 0 (build 0 errors, 0 warnings in tests).

## Concerns
- Catalog is empty, so the Configure UI paths with models are covered only by unit tests (ModelSelector/services), not run interactively; XAML compiled but not visually verified.
- Worker CUDA advertisement is a stub (`WorkerAdvertises`, default false) until the worker client exists; Auto therefore resolves to CPU.
- Installed check uses presence and size (when known), not a re-hash.
- Catalog `scale` is per-entry; entries sharing an `id` are grouped as one model with multiple scales.
