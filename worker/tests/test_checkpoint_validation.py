import hashlib
import json
import shutil
from dataclasses import replace
from pathlib import Path

import pytest

import detailframe_worker.catalog as catalog_module
from detailframe_worker.catalog import (
    CheckpointValidationError,
    load_catalog,
    validate_checkpoint,
)


REPOSITORY_ROOT = Path(__file__).parents[2]
SCHEMA_PATH = REPOSITORY_ROOT / "shared" / "models.schema.json"
MANIFEST_SCHEMA_PATH = REPOSITORY_ROOT / "shared" / "validation-manifest.schema.json"
EXPECTED_STATE = {"layer.weight": [2, 2]}


def catalog_variant(directory: Path, checkpoint: Path, state, monkeypatch):
    digest = hashlib.sha256(checkpoint.read_bytes()).hexdigest()
    directory.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(SCHEMA_PATH, directory / "models.schema.json")
    shutil.copyfile(
        MANIFEST_SCHEMA_PATH,
        directory / "validation-manifest.schema.json",
    )
    manifest = {
        "manifest_version": 1,
        "model_id": "test-x2",
        "checkpoint_sha256": digest,
        "weight_url": "https://models.example.com/test.pth",
        "license": "CC-BY-4.0",
        "provenance_url": "https://models.example.com/provenance",
        "architecture": "test_architecture",
        "scale": 2,
        "architecture_config": {"num_features": 64, "blocks": 4},
        "architecture_config_sha256": "",
        "expected_state_dict": state,
        "device_validation": {
            "cpu": {
                "status": "passed",
                "tested_at": "2026-10-19",
                "torch_version": "2.9.1+cpu",
                "checkpoint_sha256": digest,
            }
        },
    }
    digest_payload = {
        "architecture": manifest["architecture"],
        "scale": manifest["scale"],
        "config": manifest["architecture_config"],
    }
    canonical = json.dumps(
        digest_payload,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
    ).encode("utf-8")
    manifest["architecture_config_sha256"] = hashlib.sha256(canonical).hexdigest()
    manifest_path = directory / "test.manifest.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    manifest_digest = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
    monkeypatch.setattr(
        catalog_module, "_load_reviewed_manifests", lambda: {manifest_digest: "test-x2"}
    )
    entry = {
        "id": "test-x2",
        "display_name": "Test x2",
        "architecture": "test_architecture",
        "scale": 2,
        "weight_file": checkpoint.name,
        "weight_url": manifest["weight_url"],
        "sha256": digest,
        "license": "CC-BY-4.0",
        "provenance_url": manifest["provenance_url"],
        "devices": ["cpu"],
        "enabled": True,
        "validation_manifest": manifest_path.name,
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
    variant = catalog_variant(tmp_path / "catalog", checkpoint, EXPECTED_STATE, monkeypatch)

    deserialize_calls = []

    def deserialization_must_not_run(*args, **kwargs):
        deserialize_calls.append(True)
        raise AssertionError("checkpoint was deserialized before digest verification")

    monkeypatch.setattr(torch, "load", deserialization_must_not_run)
    variant = replace(variant, sha256="0" * 64)

    with pytest.raises(CheckpointValidationError):
        validate_checkpoint(variant, checkpoint)
    assert deserialize_calls == []


def test_rejects_matching_digest_with_incompatible_state_before_inference(
    tmp_path,
    monkeypatch,
):
    import torch

    checkpoint = tmp_path / "incompatible.pth"
    torch.save({"other.weight": torch.ones((2, 2))}, checkpoint)
    variant = catalog_variant(tmp_path / "catalog", checkpoint, EXPECTED_STATE, monkeypatch)

    with pytest.raises(CheckpointValidationError):
        validate_checkpoint(variant, checkpoint)


def test_rejects_matching_digest_with_incompatible_tensor_shape(tmp_path, monkeypatch):
    import torch

    checkpoint = tmp_path / "wrong-shape.pth"
    torch.save({"layer.weight": torch.ones((3, 2))}, checkpoint)
    variant = catalog_variant(tmp_path / "catalog", checkpoint, EXPECTED_STATE, monkeypatch)

    with pytest.raises(CheckpointValidationError):
        validate_checkpoint(variant, checkpoint)


def test_rejects_self_supplied_state_map_unrelated_to_reviewed_manifest(
    tmp_path,
    monkeypatch,
):
    import torch

    checkpoint = tmp_path / "forged-state-map.pth"
    torch.save({"layer.weight": torch.ones((2, 2))}, checkpoint)
    variant = catalog_variant(tmp_path / "catalog", checkpoint, EXPECTED_STATE, monkeypatch)
    variant = replace(variant, expected_state_dict=(("unrelated.weight", (2, 2)),))
    deserialize_calls = []
    monkeypatch.setattr(
        torch,
        "load",
        lambda *args, **kwargs: deserialize_calls.append(True),
    )

    with pytest.raises(CheckpointValidationError, match="reviewed architecture manifest"):
        validate_checkpoint(variant, checkpoint)
    assert deserialize_calls == []


def test_accepts_matching_state_and_loads_safely_on_cpu(tmp_path, monkeypatch):
    import torch

    checkpoint = tmp_path / "compatible.pth"
    torch.save({"layer.weight": torch.ones((2, 2))}, checkpoint)
    variant = catalog_variant(tmp_path / "catalog", checkpoint, EXPECTED_STATE, monkeypatch)
    real_load = torch.load
    load_options = {}

    def record_safe_load(*args, **kwargs):
        load_options.update(kwargs)
        return real_load(*args, **kwargs)

    monkeypatch.setattr(torch, "load", record_safe_load)

    validate_checkpoint(variant, checkpoint)

    assert load_options == {"map_location": "cpu", "weights_only": True}
