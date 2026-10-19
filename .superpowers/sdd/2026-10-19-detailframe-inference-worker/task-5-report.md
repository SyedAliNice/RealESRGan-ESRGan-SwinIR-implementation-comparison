# Task 5 implementation report

## Result

Implemented the CPU inference engine and wired it into the worker's JSON-lines
job loop. The engine loads only catalog variants that passed Task 4's reviewed
validation-manifest gate, dispatches by the catalog architecture, validates
the CPU device/scale before checkpoint loading, and uses `torch.inference_mode`
for tiled inference. It preserves source files, verifies a temporary encoded
image can be reopened, and atomically replaces the output only after success.
Progress and exactly one terminal completion/failure/cancellation event are
correlated to the active job ID.

No real model candidate was verified. `shared/models.json`, the production
adapter registry, and `REVIEWED_VALIDATION_MANIFESTS` remain empty. The
production CPU and worker integration tests assert that the empty catalog
returns `MODEL_UNAVAILABLE` without creating output. A deterministic
nearest-neighbour adapter exists only in tests.

Also completed Task 3's deferred Pillow limit fix: image validation temporarily
sets Pillow's decompression threshold to the configured cap under a lock,
restores the prior global value in `finally`, accepts a valid image just below
the cap, and rejects images at or above it.

## TDD and verification

**RED — before adding the engine**

Command:

```text
uv run --directory worker python -m pytest tests/test_engine_unit.py tests/test_images.py -q
```

Output: collection failed because `detailframe_worker.engine` did not yet
exist (`ImportError: cannot import name 'engine'`); `1 error` (exit code 2).

**GREEN — focused engine and image tests**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests/test_engine_unit.py worker/tests/test_images.py -q
```

Output:

```text
.....................................                                    [100%]
37 passed in 3.18s
```

**GREEN — production CPU catalog integration**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests/integration/test_verified_model_cpu.py -q
```

Output:

```text
..                                                                       [100%]
2 passed in 2.35s
```

**GREEN — worker/protocol integration**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests/test_worker_engine_integration.py worker/tests/test_worker_lifecycle.py -q
```

Output:

```text
.............                                                            [100%]
13 passed in 2.35s
```

The integration coverage verifies deterministic CPU enhancement, job-correlated
progress, completion after output verification, cancellation cleanup, and the
empty production catalog's `MODEL_UNAVAILABLE` response without output.

**GREEN — full worker suite**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests -q
```

Output:

```text
........................................................................ [ 44%]
........................................................................ [ 88%]
..................                                                       [100%]
162 passed in 3.73s
```

`git diff --cached --check` passed with no output. Only the eight Task 5 worker
source/test files were staged and committed; the untracked
`docs/superpowers/plans/` directory was left untouched.

## Commit

`cb96cf5 feat(worker): wire CPU inference engine`

## Concern

Production cannot yet enhance images because there are no verified model
artifacts or reviewed architecture adapters. The worker now correctly reports
that state rather than accepting an unverified or test-only model.

## Task 5 fix round 1

### Changes

- Added a worker cancellation token that serializes cancellation requests with
  the final cancellation check and atomic output replacement. Cancellation
  that obtains the gate before publication aborts and leaves the existing
  output untouched; once publication holds the gate and completes, completion
  wins and a waiting cancellation is too late.
- Moved the 100% encoding progress event until after `os.replace` succeeds.
- Added deterministic coverage for cancellation while replacement is held at
  the commit boundary, and for replacement failure preserving prior output,
  removing the temporary file, and not reporting 100%.

### Fix verification

The first focused run exposed a missing `is_set()` compatibility method on the
new token (6 failures); it was added before the successful rerun.

**Engine and worker protocol integration**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests/test_engine_unit.py worker/tests/test_worker_engine_integration.py worker/tests/test_worker_lifecycle.py -q
```

Output:

```text
........................................                                 [100%]
40 passed in 1.88s
```

**Full worker suite**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests -q
```

Output:

```text
........................................................................ [ 43%]
........................................................................ [ 87%]
....................                                                     [100%]
164 passed in 3.73s
```

### Self-review

Reviewed the complete engine, worker, and affected test files. The token gate
is shared by the worker's cancellation setter and the engine's final check and
`os.replace`; completion is therefore consistent with the publication that
actually committed. The new tests cover both sides of the requested boundary
and replacement failure. No additional issues found.
