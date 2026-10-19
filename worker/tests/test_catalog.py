import json
import shutil
from pathlib import Path

import pytest

from detailframe_worker.catalog import (
    CatalogValidationError,
    DeviceValidation,
    load_catalog,
)


REPOSITORY_ROOT = Path(__file__).parents[2]
SCHEMA_PATH = REPOSITORY_ROOT / "shared" / "models.schema.json"
TEST_DIGEST = "a" * 64


def valid_entry():
    return {
        "id": "test-x2",
        "display_name": "Test x2",
        "architecture": "test_architecture",
        "scale": 2,
        "weight_file": "test.pth",
        "weight_url": "https://models.example.invalid/test.pth",
        "sha256": TEST_DIGEST,
        "license": "CC-BY-4.0",
        "provenance_url": "https://models.example.invalid/provenance",
        "devices": ["cpu"],
        "enabled": True,
        "validation": {
            "cpu": {
                "status": "passed",
                "tested_at": "2026-10-19",
                "torch_version": "2.9.1+cpu",
                "checkpoint_sha256": TEST_DIGEST,
            }
        },
        "expected_state_dict": {"layer.weight": [2, 2]},
    }


def write_catalog(directory: Path, entries):
    directory.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(SCHEMA_PATH, directory / "models.schema.json")
    path = directory / "models.json"
    path.write_text(
        json.dumps({"schema_version": 1, "models": entries}),
        encoding="utf-8",
    )
    return path


def test_loads_empty_version_one_catalog():
    assert load_catalog(REPOSITORY_ROOT / "shared" / "models.json") == ()


def test_loads_evidence_complete_cpu_variant(tmp_path):
    catalog = load_catalog(write_catalog(tmp_path, [valid_entry()]))

    assert catalog[0].enabled is True
    assert catalog[0].devices == ("cpu",)
    assert catalog[0].architecture == "test_architecture"
    assert catalog[0].scale == 2
    assert catalog[0].expected_state_dict == (("layer.weight", (2, 2)),)
    assert catalog[0].validation == (
        (
            "cpu",
            DeviceValidation(
                status="passed",
                tested_at="2026-10-19",
                torch_version="2.9.1+cpu",
                checkpoint_sha256=TEST_DIGEST,
            ),
        ),
    )


@pytest.mark.parametrize(
    "field, mutate",
    [
        ("weight_url", lambda entry: entry.update(weight_url="")),
        ("weight_file path", lambda entry: entry.update(weight_file="..")),
        ("provenance_url", lambda entry: entry.update(provenance_url="")),
        ("sha256", lambda entry: entry.update(sha256="A" * 64)),
        ("license", lambda entry: entry.update(license="")),
        ("architecture", lambda entry: entry.update(architecture="")),
        ("scale", lambda entry: entry.update(scale=0)),
        (
            "cpu validation",
            lambda entry: entry.update(validation={}),
        ),
        (
            "cuda validation",
            lambda entry: entry.update(devices=["cpu", "cuda"]),
        ),
        (
            "validation digest",
            lambda entry: entry["validation"]["cpu"].update(
                checkpoint_sha256="b" * 64
            ),
        ),
    ],
)
def test_rejects_enabled_variant_without_required_evidence(field, mutate, tmp_path):
    entry = valid_entry()
    mutate(entry)
    path = write_catalog(tmp_path / field.replace(" ", "_"), [entry])

    with pytest.raises(CatalogValidationError):
        load_catalog(path)
