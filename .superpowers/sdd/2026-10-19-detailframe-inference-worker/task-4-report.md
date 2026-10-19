# Task 4 implementation report

## Result

Implemented the version-1 shared model catalog schema and loader. Enabled
variants require HTTPS weight and provenance URLs, a lowercase SHA-256,
non-empty license and architecture, a valid scale, CPU validation, validation
for every advertised device bound to the same checkpoint digest, and an
expected state-dict key/shape map. Weight filenames are restricted to safe
single-component names. The initial shared catalog has no model entries:
notebook/archive contents were not treated as proof of official provenance,
license, digest, architecture mapping, or successful CPU validation.

Checkpoint validation hashes the file in 1 MiB chunks, checks optional size,
and only then calls `torch.load(map_location="cpu", weights_only=True)` on the
same open file. It rejects disabled variants and checkpoint state dictionaries
with unexpected/missing keys or incompatible tensor shapes.

Pinned `torch==2.9.1+cpu` to the explicit official CPU index
`https://download.pytorch.org/whl/cpu` and locked resolved dependencies in
`worker/uv.lock`. The existing Task 1 verification of the official index
recorded Windows x86-64 wheels for CPython 3.11–3.14, including their SHA-256
hashes. On this Windows host, Python 3.14.7 installed the locked CPU wheel and
passed a CPU matrix-multiplication smoke check (`torch.version.cuda is None`).
Also pinned `jsonschema==4.25.1` and `numpy==2.3.3`.

## TDD and verification

**RED — missing catalog implementation**

Command:

```text
py -m pytest worker/tests/test_catalog.py worker/tests/test_checkpoint_validation.py -q
```

Output: both test modules failed collection with
`ModuleNotFoundError: No module named 'detailframe_worker.catalog'`;
`2 errors in 0.23s` (exit code 2).

**RED — filename traversal hardening**

Command:

```text
uv run --project worker --extra dev python -m pytest worker/tests/test_catalog.py -q
```

Output: `1 failed, 11 passed in 1.25s`; the new `weight_file=".."` case did not
raise. Restricted schema filenames to safe basename characters.

**GREEN — focused tests, Python 3.14.7**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests/test_catalog.py worker/tests/test_checkpoint_validation.py -q
```

Output:

```text
................                                                         [100%]
16 passed in 13.10s
```

**GREEN — full worker suite, Python 3.14.7**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests -q
```

Output:

```text
...................................................                      [100%]
51 passed in 6.05s
```

Additional verification:

- `uv lock --project worker --check` — passed (`Resolved 25 packages in 4ms`).
- `uv run --project worker --python 3.14 python -c "import torch; x=torch.ones((2,2)); assert torch.equal(x @ x, torch.full((2,2), 2.0)); assert torch.__version__ == '2.9.1+cpu' and torch.version.cuda is None; print(f'PyTorch {torch.__version__} CPU matmul passed; CUDA: {torch.version.cuda}')"` — printed `PyTorch 2.9.1+cpu CPU matmul passed; CUDA: None`.
- `git diff --cached --check` — passed with no output.

## Self-review and commit

Reviewed the schema, loader, tests, and dependency source/lock. Confirmed the
wrong-digest test asserts the deserializer was never called; matching digests
with wrong keys or shapes are rejected. Only the seven Task 4 implementation
files (including the dependency lock) were staged; existing plan documents and
SDD ledgers were left unstaged.

Commit: `802143c feat(worker): add evidence-gated model catalog`

## Concern

The catalog is intentionally empty, so no real model is exposed for inference
until official provenance, licensing, checkpoint digest, architecture mapping,
and a passing CPU validation are available.

## Reviewer fix round 1

The production catalog remains empty and `REVIEWED_VALIDATION_MANIFESTS` is an
empty trust allowlist. An enabled entry now requires a separate validation
manifest whose exact SHA-256 has been explicitly reviewed and pinned in the
worker. The manifest binds the checkpoint digest, model ID, license, weight and
provenance URLs, advertised device tests, architecture, scale, configuration,
and expected state-dict key/shape map. The configuration digest is computed
over canonical architecture/scale/configuration data; the full manifest digest
also binds the expected state map and provenance references. Checkpoint
validation rechecks this attestation before deserialization, so a caller cannot
substitute an unrelated expected-state map. No candidate support or trust
allowlist entry was invented.

The catalog schema rejects Windows device basenames (including case variants
and extensions) and URL declarations without a parsed HTTPS hostname or with
username/password credentials. Tests cover fake `.invalid` evidence, disabled
unverified entries remaining outside the eligible set, manifest/config
tampering, and self-supplied state maps.

**Focused catalog and checkpoint validation**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests/test_catalog.py worker/tests/test_checkpoint_validation.py -q
```

Output:

```text
.............................................                            [100%]
45 passed in 2.10s
```

**Full worker suite**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests -q
```

Output:

```text
........................................................................ [ 90%]
........                                                                 [100%]
80 passed in 2.44s
```

**Self-review**

`git diff --check` — passed with no output. Reviewed the schema/loader binding,
failure paths before `torch.load`, Windows filename and parsed URL checks, and
the explicit disabled/unverified eligibility test. Only Task 4 catalog,
manifest-schema, loader, tests, and report files are in scope; existing plan
and ledger artifacts were left untouched.

## Reviewer fix round 2

HTTPS URL format validation now parses the authority with `urllib.parse`,
accesses `.port` so malformed or out-of-range ports are rejected, and requires
an HTTPS scheme, hostname, no credentials, and a non-root resource path. Tests
cover malformed and out-of-range ports and missing paths. The validation
manifest schema is now worker package data and is read via
`importlib.resources`, so loading an empty catalog no longer depends on a
second schema file beside the catalog.

**Focused catalog and checkpoint validation**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests/test_catalog.py worker/tests/test_checkpoint_validation.py -q
```

Output:

```text
....................................................                     [100%]
52 passed in 2.43s
```

**Full worker suite**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests -q
```

Output:

```text
........................................................................ [ 82%]
...............                                                          [100%]
87 passed in 2.70s
```

**Packaged-resource verification**

Command:

```text
uv build --project worker --wheel
```

Output: `Successfully built worker\dist\detailframe_worker-1.0.0-py3-none-any.whl`;
archive inspection confirmed
`detailframe_worker/data/validation-manifest.schema.json` is present. The
focused suite also passed an empty catalog load from a directory without an
adjacent validation-manifest schema.

**Self-review**

Reviewed malformed-port handling, scheme/host/credential/path requirements,
resource loading from the package, and package-data configuration. The
production catalog and reviewed-manifest allowlist remain empty; no models
were enabled. Plans and ledgers remain untouched.

## Reviewer fix round 3

HTTPS URL validation now compares the parsed scheme case-insensitively and
validates the authority instead of trusting `urlsplit().hostname`. It rejects
backslashes and raw whitespace, credentials, malformed DNS labels and
characters, invalid punycode, malformed IPv4/IPv6 literals, bracket misuse,
empty/non-numeric/out-of-range ports, and extra authority colons. DNS names are
validated after IDNA encoding, so valid Unicode IDNs and their punycode forms
remain supported. The shared catalog schema and both copies of the validation
manifest schema now use a case-insensitive HTTPS pattern that also excludes
backslashes and whitespace from the authority. Existing path, query, and
fragment behavior is preserved.

Focused tests cover rejected malformed hosts and ports for both URL fields,
plus acceptance of case-insensitive HTTPS, valid ports, IDN and punycode,
IPv4/IPv6 literals, and resource paths containing query/fragment components.
The empty catalog, packaged manifest schema loading, and empty reviewed
manifest allowlist remain unchanged; no models are enabled.

**Focused catalog and checkpoint validation**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests/test_catalog.py worker/tests/test_checkpoint_validation.py -q
```

Output:

```text
........................................................................ [ 78%]
....................                                                     [100%]
92 passed in 4.04s
```

**Full worker suite**

Command:

```text
uv run --project worker --python 3.14 --extra dev python -m pytest worker/tests -q
```

Output:

```text
........................................................................ [ 56%]
.......................................................                  [100%]
127 passed in 3.94s
```

**Patch hygiene**

Command:

```text
git diff --check
```

Output: `git diff --check: passed`.

**Self-review**

Reviewed URL scheme parsing, authority and port handling, DNS/IDNA/IP
validation, schema parity, and the focused acceptance/rejection cases.
Confirmed `shared/models.json` still contains an empty version-1 catalog and
`REVIEWED_VALIDATION_MANIFESTS` remains empty. The packaged manifest schema is
still loaded through `importlib.resources`. Only Task 4 URL validation,
schemas, tests, and this report are changed; untracked plan documents and
ledgers remain untouched.
