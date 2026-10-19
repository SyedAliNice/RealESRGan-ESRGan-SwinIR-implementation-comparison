import hashlib
import json
import shutil
from pathlib import Path

import pytest

import detailframe_worker.catalog as catalog_module
from detailframe_worker.catalog import CatalogValidationError, load_catalog


REPOSITORY_ROOT = Path(__file__).parents[2]
SHARED_ROOT = REPOSITORY_ROOT / "shared"
TEST_DIGEST = "a" * 64


def valid_entry():
    return {
        "id": "test-x2",
        "display_name": "Test x2",
        "architecture": "test_architecture",
        "scale": 2,
        "weight_file": "test.pth",
        "weight_url": "https://models.example.com/test.pth",
        "sha256": TEST_DIGEST,
        "license": "CC-BY-4.0",
        "provenance_url": "https://models.example.com/provenance",
        "devices": ["cpu"],
        "enabled": True,
        "validation_manifest": "test.manifest.json",
    }


def manifest_for(entry):
    manifest = {
        "manifest_version": 1,
        "model_id": entry["id"],
        "checkpoint_sha256": entry["sha256"],
        "weight_url": entry["weight_url"],
        "license": entry["license"],
        "provenance_url": entry["provenance_url"],
        "architecture": entry["architecture"],
        "scale": entry["scale"],
        "architecture_config": {"num_features": 64, "blocks": 4},
        "architecture_config_sha256": "",
        "expected_state_dict": {"layer.weight": [2, 2]},
        "device_validation": {
            "cpu": {
                "status": "passed",
                "tested_at": "2026-10-19",
                "torch_version": "2.9.1+cpu",
                "checkpoint_sha256": entry["sha256"],
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
    return manifest


def write_catalog(directory: Path, entries):
    directory.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(SHARED_ROOT / "models.schema.json", directory / "models.schema.json")
    shutil.copyfile(
        SHARED_ROOT / "validation-manifest.schema.json",
        directory / "validation-manifest.schema.json",
    )
    for entry in entries:
        if entry.get("enabled") and entry.get("validation_manifest"):
            raw_manifest = json.dumps(manifest_for(entry)).encode("utf-8")
            (directory / entry["validation_manifest"]).write_bytes(raw_manifest)
    catalog_path = directory / "models.json"
    catalog_path.write_text(
        json.dumps({"schema_version": 1, "models": entries}),
        encoding="utf-8",
    )
    return catalog_path


def test_loads_empty_version_one_catalog():
    assert load_catalog(SHARED_ROOT / "models.json") == ()


def test_loads_empty_catalog_without_adjacent_manifest_schema(tmp_path):
    shutil.copyfile(SHARED_ROOT / "models.schema.json", tmp_path / "models.schema.json")
    path = tmp_path / "models.json"
    path.write_text(json.dumps({"schema_version": 1, "models": []}), encoding="utf-8")

    assert load_catalog(path) == ()


def test_loads_catalog_variant_only_with_test_trusted_manifest(tmp_path, monkeypatch):
    entry = valid_entry()
    path = write_catalog(tmp_path, [entry])
    digest = hashlib.sha256((tmp_path / entry["validation_manifest"]).read_bytes()).hexdigest()
    monkeypatch.setitem(catalog_module.REVIEWED_VALIDATION_MANIFESTS, digest, entry["id"])

    variant, = load_catalog(path)

    assert variant.enabled is True
    assert variant.devices == ("cpu",)
    assert variant.architecture == "test_architecture"
    assert variant.scale == 2
    assert variant.expected_state_dict == (("layer.weight", (2, 2)),)
    assert variant.architecture_config_json == '{"blocks":4,"num_features":64}'
    assert variant.architecture_config_sha256 == manifest_for(entry)[
        "architecture_config_sha256"
    ]
    assert variant.validation_manifest_sha256 == digest
    assert variant.validation[0][0] == "cpu"


def test_unverified_fake_invalid_evidence_cannot_enable_model(tmp_path):
    entry = valid_entry()
    entry["weight_url"] = "https://models.example.invalid/test.pth"
    entry["provenance_url"] = "https://models.example.invalid/provenance"
    path = write_catalog(tmp_path, [entry])

    with pytest.raises(CatalogValidationError, match="not been independently reviewed"):
        load_catalog(path)


@pytest.mark.parametrize(
    "url",
    ["https://", "https://user:pass@models.example.com/file"],
)
def test_rejects_reviewed_manifest_url_without_host_or_with_credentials(
    url,
    tmp_path,
    monkeypatch,
):
    entry = valid_entry()
    path = write_catalog(tmp_path, [entry])
    manifest_path = tmp_path / entry["validation_manifest"]
    manifest = json.loads(manifest_path.read_bytes())
    manifest["weight_url"] = url
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    digest = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
    monkeypatch.setitem(catalog_module.REVIEWED_VALIDATION_MANIFESTS, digest, entry["id"])

    with pytest.raises(CatalogValidationError):
        load_catalog(path)


def test_disabled_unverified_entry_is_not_eligible(tmp_path):
    entry = valid_entry()
    entry["enabled"] = False
    entry.pop("validation_manifest")
    entry["weight_url"] = "https://models.example.invalid/test.pth"
    entry["provenance_url"] = "https://models.example.invalid/provenance"
    catalog = load_catalog(write_catalog(tmp_path, [entry]))

    assert len(catalog) == 1
    assert catalog[0].enabled is False
    assert tuple(variant for variant in catalog if variant.enabled) == ()


@pytest.mark.parametrize(
    "filename",
    [
        "CON",
        "con",
        "CON.txt",
        "PrN.png",
        "AUX",
        "aux.data",
        "NUL",
        "nul.pth",
        "COM1",
        "com9.bin",
        "LPT1",
        "lpt9.weights",
    ],
)
def test_rejects_windows_reserved_device_filenames(filename, tmp_path):
    entry = valid_entry()
    entry["enabled"] = False
    entry["weight_file"] = filename

    with pytest.raises(CatalogValidationError):
        load_catalog(write_catalog(tmp_path / f"case-{filename.lower()}", [entry]))


@pytest.mark.parametrize(
    "url",
    [
        "https://",
        "https:///missing-host",
        "https://user@models.example.com/file",
        "https://models.example.com:bad/file",
        "https://models.example.com:99999/file",
        "https://models.example.com",
        "https://user:pass@models.example.com/file",
        "https://:pass@models.example.com/file",
        "https://models.example.com\\evil/file",
        "https://models example.com/file",
        "https://-models.example.com/file",
        "https://models-.example.com/file",
        "https://models..example.com/file",
        "https://models_example.com/file",
        "https://[::1/file",
        "https://[not-an-ip]/file",
        "https://[192.0.2.1]/file",
        "https://::1/file",
        "https://999.999.1.1/file",
        "https://models.example.com:/file",
        "https://models.example.com:0/file",
        "https://models.example.com:65536/file",
        "https://models.example.com:443:444/file",
    ],
)
@pytest.mark.parametrize("field", ["weight_url", "provenance_url"])
def test_rejects_https_urls_without_host_or_with_credentials(url, field, tmp_path):
    entry = valid_entry()
    entry["enabled"] = False
    entry[field] = url

    with pytest.raises(CatalogValidationError):
        load_catalog(write_catalog(tmp_path / field, [entry]))


@pytest.mark.parametrize(
    "url",
    [
        "HTTPS://models.example.com:8443/file?token=example#download",
        "https://xn--bcher-kva.example/file",
        "https://bücher.example/file",
        "https://192.0.2.1/file",
        "https://[2001:db8::1]:443/file",
    ],
)
@pytest.mark.parametrize("field", ["weight_url", "provenance_url"])
def test_accepts_valid_https_hosts_and_resource_urls(url, field, tmp_path):
    entry = valid_entry()
    entry["enabled"] = False
    entry[field] = url

    assert len(load_catalog(write_catalog(tmp_path / field, [entry]))) == 1


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
            lambda entry: entry.update(devices=["cuda"]),
        ),
        (
            "manifest reference",
            lambda entry: entry.pop("validation_manifest"),
        ),
    ],
)
def test_rejects_enabled_variant_without_required_evidence(field, mutate, tmp_path):
    entry = valid_entry()
    mutate(entry)
    path = write_catalog(tmp_path / field.replace(" ", "_"), [entry])

    with pytest.raises(CatalogValidationError):
        load_catalog(path)


def test_rejects_self_supplied_validation_and_expected_state_map(tmp_path):
    entry = valid_entry()
    entry["validation"] = {
        "cpu": {
            "status": "passed",
            "checkpoint_sha256": TEST_DIGEST,
        }
    }
    entry["expected_state_dict"] = {"unrelated.weight": [2, 2]}

    with pytest.raises(CatalogValidationError):
        load_catalog(write_catalog(tmp_path, [entry]))


def test_rejects_catalog_architecture_different_from_reviewed_manifest(tmp_path, monkeypatch):
    entry = valid_entry()
    path = write_catalog(tmp_path, [entry])
    digest = hashlib.sha256((tmp_path / entry["validation_manifest"]).read_bytes()).hexdigest()
    monkeypatch.setitem(catalog_module.REVIEWED_VALIDATION_MANIFESTS, digest, entry["id"])
    entry["architecture"] = "different_architecture"
    path.write_text(
        json.dumps({"schema_version": 1, "models": [entry]}),
        encoding="utf-8",
    )

    with pytest.raises(CatalogValidationError, match="does not match its validation manifest"):
        load_catalog(path)


def test_rejects_manifest_with_wrong_architecture_config_digest(tmp_path, monkeypatch):
    entry = valid_entry()
    path = write_catalog(tmp_path, [entry])
    manifest_path = tmp_path / entry["validation_manifest"]
    manifest = json.loads(manifest_path.read_bytes())
    manifest["architecture_config"]["num_features"] = 128
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    digest = hashlib.sha256(manifest_path.read_bytes()).hexdigest()
    monkeypatch.setitem(catalog_module.REVIEWED_VALIDATION_MANIFESTS, digest, entry["id"])

    with pytest.raises(CatalogValidationError, match="architecture/configuration digest"):
        load_catalog(path)
