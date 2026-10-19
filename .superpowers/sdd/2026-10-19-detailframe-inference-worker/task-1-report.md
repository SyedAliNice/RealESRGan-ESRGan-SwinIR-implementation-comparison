# Task 1 Implementation Report

**Status:** DONE
**Commit:** `5718d47` — `feat(worker): establish Python package`

## Red/green evidence

Before implementation, `py -m pytest worker/tests/test_package.py -q` failed during collection as expected:

```text
ModuleNotFoundError: No module named 'detailframe_worker'
1 error in 0.33s
```

Installed the package with `py -m pip install -e "worker[dev]"` successfully.
The focused test then passed:

```text
.                                                                        [100%]
1 passed in 0.02s
```

The full Task 1 worker suite passed without collected-test warnings:

```text
.                                                                        [100%]
1 passed in 0.01s
```

Both `detailframe-worker --version` and `py -m detailframe_worker --version`
reported `detailframe-worker 1.0.0`. Normal-mode invocations lacking either
`--protocol-stdio` or `--models-dir` exited with argparse status 2.

## Files implemented

- `worker/pyproject.toml` — src-layout package metadata, Python `>=3.11,<3.15`,
  pinned setuptools and pytest development dependency, and console entry point.
- `worker/src/detailframe_worker/__init__.py` — package version.
- `worker/src/detailframe_worker/__main__.py` — parser and module/console entry
  points; `--version` exits independently, while normal operation requires both
  protocol and model-directory arguments.
- `worker/tests/test_package.py` — protocol-mode parsing test.
- `worker/.gitignore` — Python, pytest, editable-install, and build artifacts.

## Self-review

- Reviewed the staged diff and confirmed it contains only the five Task 1
  implementation files; `git diff --cached --check` passed.
- Verified `models_dir` is converted to `Path` and the test asserts both parsed
  values.
- Verified version handling occurs through argparse's independent version
  action, before normal-operation validation.
- The existing untracked plan documents were not staged or committed.
- Protocol processing itself remains for Task 2, as scoped by the approved plan.

## Review finding fix: Python/PyTorch CPU wheel compatibility

**Finding:** `worker/pyproject.toml:9` advertised Python 3.11–3.14 without
identifying or verifying a compatible Windows CPU PyTorch wheel.

The official PyTorch CPU wheel index
(`https://download.pytorch.org/whl/cpu/torch/`) lists Windows x86-64
`torch-2.9.1+cpu` wheels for each CPython ABI in the declared range:

- CPython 3.11: `torch-2.9.1+cpu-cp311-cp311-win_amd64.whl`
  (SHA-256 `69b3785d28be5a9c56ab525788ec5000349ec59132a74b7d5e954b905015b992`)
- CPython 3.12: `torch-2.9.1+cpu-cp312-cp312-win_amd64.whl`
  (SHA-256 `a4e06b4f441675d26b462123c8a83e77c55f1ec8ebc081203be2db1ea8054add`)
- CPython 3.13: `torch-2.9.1+cpu-cp313-cp313-win_amd64.whl`
  (SHA-256 `404a7ab2fffaf2ca069e662f331eb46313692b2f1630df2720094284f390ccef`)
- CPython 3.14: `torch-2.9.1+cpu-cp314-cp314-win_amd64.whl`
  (SHA-256 `f60e2565f261542efac07e25208fb3fc55c6fe82314a5a9cbee971edb5f27713`)

Kept `requires-python = ">=3.11,<3.15"` because this exact version's official
CPU index provides Windows wheels for all four interpreter versions. Added a
focused metadata test to guard the declared range. The wheel matrix establishes
availability, not a full inference/runtime smoke test; Task 2 still needs to
pin the selected PyTorch version and test it in the bundled runtime.

### Fix verification

Commands run and results:

- PowerShell:
  ```powershell
  $html = (Invoke-WebRequest -Uri 'https://download.pytorch.org/whl/cpu/torch/' -UseBasicParsing).Content
  [regex]::Matches($html, '<a href="([^"]*torch-2\.9\.1(?:%2B|\+)cpu-cp31[1-4]-cp31[1-4]-win_amd64\.whl[^"]*)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
  ```
  Result: exactly the cp311, cp312, cp313, and cp314 `win_amd64` wheel links
  were returned; their SHA-256 values are recorded above.
- `py -m pytest worker/tests/test_package.py -q` — **2 passed**.
- `py -m pytest worker/tests -q` — **2 passed**, no warnings.
- `git diff --check` — passed.

### Fix self-review

- Confirmed the range covers exactly the CPython versions represented by the
  verified wheel matrix; Python 3.15 is excluded.
- Confirmed the metadata guard reads the project metadata with Python's
  standard-library TOML parser and checks the declared range.
- Only the Task 1 metadata guard and this report are included in the fix; the
  existing `worker/pyproject.toml` range is unchanged. The pre-existing
  untracked `docs/superpowers/plans/` documents were left untouched.
