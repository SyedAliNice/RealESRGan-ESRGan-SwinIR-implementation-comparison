import hashlib
import json
import shutil
from dataclasses import replace
from pathlib import Path

import pytest

from detailframe_worker.catalog import (
    CheckpointValidationError,
    load_catalog,
    validate_checkpoint,
)


REPOSITORY_ROOT = Path(__file__).parents[2]
SCHEMA_PATH = REPOSITORY_ROOT / "shared" / "models.schema.json"
EXPECTED_STATE = {"layer.weight": [2, 2]}


def catalog_variant(directory: Path, checkpoint: Path, state):
    digest = hashlib.sha256(checkpoint.read_bytes()).hexdigest()
    directory.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(SCHEMA_PATH, directory / "models.schema.json")
    entry = {
        "id": "test-x2",
        "display_name": "Test x2",
        "architecture": "test_architecture",
        "scale": 2,
        "weight_file": checkpoint.name,
        "weight_url": "https://models.example.invalid/test.pth",
        "sha256": digest,
        "license": "CC-BY-4.0",
        "provenance_url": "https://models.example.invalid/provenance",
        "devices": ["cpu"],
        "enabled": True,
        "validation": {
            "cpu": {
                "status": "passed",
                "tested_at": "2026-10-19",
                "torch_version": "2.9.1+cpu",
                "checkpoint_sha256": digest,
            }
        },
        "expected_state_dict": state,
    }
    catalog_path = directory / "models.json"
    catalog_path.write_text(
        json.dumps({"schema_version": 1, "models": [entry]}),
        encoding="utf-8",
    )
    return load_catalog(catalog_path)[0]


def test_wrong_digest_is_rejected_before_checkpoint_deserialization(tmp_path, monkeypatch):
    import torch

    checkpoint = tmp_path / "wrong.pth"
    checkpoint.write_bytes(b"checkpoint content with the wrong digest")
    variant = catalog_variant(tmp_path / "catalog", checkpoint, EXPECTED_STATE)

    deserialize_calls = []

    def deserialization_must_not_run(*args, **kwargs):
        deserialize_calls.append(True)
        raise AssertionError("checkpoint was deserialized before digest verification")

    monkeypatch.setattr(torch, "load", deserialization_must_not_run)
    variant = replace(variant, sha256="0" * 64)

    with pytest.raises(CheckpointValidationError):
        validate_checkpoint(variant, checkpoint)
    assert deserialize_calls == []


def test_rejects_matching_digest_with_incompatible_state_before_inference(tmp_path):
    import torch

    checkpoint = tmp_path / "incompatible.pth"
    torch.save({"other.weight": torch.ones((2, 2))}, checkpoint)
    variant = catalog_variant(tmp_path / "catalog", checkpoint, EXPECTED_STATE)

    with pytest.raises(CheckpointValidationError):
        validate_checkpoint(variant, checkpoint)


def test_rejects_matching_digest_with_incompatible_tensor_shape(tmp_path):
    import torch

    checkpoint = tmp_path / "wrong-shape.pth"
    torch.save({"layer.weight": torch.ones((3, 2))}, checkpoint)
    variant = catalog_variant(tmp_path / "catalog", checkpoint, EXPECTED_STATE)

    with pytest.raises(CheckpointValidationError):
        validate_checkpoint(variant, checkpoint)


def test_accepts_matching_state_and_loads_safely_on_cpu(tmp_path, monkeypatch):
    import torch

    checkpoint = tmp_path / "compatible.pth"
    torch.save({"layer.weight": torch.ones((2, 2))}, checkpoint)
    variant = catalog_variant(tmp_path / "catalog", checkpoint, EXPECTED_STATE)
    real_load = torch.load
    load_options = {}

    def record_safe_load(*args, **kwargs):
        load_options.update(kwargs)
        return real_load(*args, **kwargs)

    monkeypatch.setattr(torch, "load", record_safe_load)

    validate_checkpoint(variant, checkpoint)

    assert load_options == {"map_location": "cpu", "weights_only": True}
