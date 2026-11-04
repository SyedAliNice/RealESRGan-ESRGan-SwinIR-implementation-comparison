import dataclasses
import io
import json

import pytest
import torch
from PIL import Image

from detailframe_worker import engine
from detailframe_worker.engine import ModelAdapter
from detailframe_worker.protocol import ProtocolError
from detailframe_worker.worker import run_worker
from test_cancellation import Nearest, variant


def cuda_variant():
    return dataclasses.replace(variant(), devices=("cpu", "cuda"))


@pytest.fixture
def source(tmp_path):
    path = tmp_path / "source.png"
    Image.new("RGB", (4, 4), "red").save(path)
    return path


def enhance(tmp_path, source, device, catalog, monkeypatch):
    monkeypatch.setattr(engine, "load_catalog", lambda _path: catalog)
    monkeypatch.setattr(engine, "_load_state_dict", lambda *_a: {})
    monkeypatch.setitem(engine.ADAPTERS, "test-nearest", ModelAdapter(lambda _v, _s: Nearest()))
    return engine.enhance(
        "test-model", 2, device, source, tmp_path / "out.png", "png",
        lambda *_a: None, lambda: False, models_dir=tmp_path,
    )


def test_cpu_and_auto_work_when_cuda_unavailable(tmp_path, source, monkeypatch):
    monkeypatch.setattr(torch.cuda, "is_available", lambda: False)
    for device in ("cpu", "auto"):
        assert enhance(tmp_path, source, device, (cuda_variant(),), monkeypatch).width == 8
    assert engine.resolve_device("auto", cuda_variant()) == "cpu"


def test_explicit_cuda_unavailable_fails_without_cpu_fallback(tmp_path, source, monkeypatch):
    monkeypatch.setattr(torch.cuda, "is_available", lambda: False)
    monkeypatch.setattr(engine, "_load_state_dict", lambda *_a: pytest.fail("no model load"))

    with pytest.raises(ProtocolError) as error:
        enhance(tmp_path, source, "cuda", (cuda_variant(),), monkeypatch)
    assert error.value.code == "DEVICE_UNAVAILABLE"
    assert not (tmp_path / "out.png").exists()


def test_cuda_available_but_model_not_validated_stays_unavailable(tmp_path, source, monkeypatch):
    monkeypatch.setattr(torch.cuda, "is_available", lambda: True)

    with pytest.raises(ProtocolError) as error:
        enhance(tmp_path, source, "cuda", (variant(),), monkeypatch)
    assert error.value.code == "DEVICE_UNAVAILABLE"
    assert engine.resolve_device("auto", variant()) == "cpu"
    assert not (tmp_path / "out.png").exists()


def test_cuda_validated_and_available_is_selectable(monkeypatch):
    monkeypatch.setattr(torch.cuda, "is_available", lambda: True)
    assert engine.resolve_device("cuda", cuda_variant()) == "cuda"
    assert engine.resolve_device("auto", cuda_variant()) == "cuda"


def capabilities_devices(tmp_path):
    stdout = io.StringIO()
    line = json.dumps(
        {"protocol_version": 1, "type": "capabilities",
         "request_id": "00000000-0000-4000-8000-000000000001"}
    ) + "\n"
    run_worker(io.StringIO(line), stdout, io.StringIO(), tmp_path)
    return json.loads(stdout.getvalue().splitlines()[0])["devices"]


def test_capabilities_advertise_only_validated_devices(tmp_path, monkeypatch):
    monkeypatch.setattr(torch.cuda, "is_available", lambda: True)
    monkeypatch.setattr(engine, "load_catalog", lambda _path: (variant(),))
    assert capabilities_devices(tmp_path) == ["cpu"]

    monkeypatch.setattr(engine, "load_catalog", lambda _path: (cuda_variant(),))
    assert capabilities_devices(tmp_path) == ["cpu", "cuda"]

    monkeypatch.setattr(torch.cuda, "is_available", lambda: False)
    assert capabilities_devices(tmp_path) == ["cpu"]


def test_auto_without_validated_cpu_or_cuda_is_device_unavailable(monkeypatch):
    monkeypatch.setattr(torch.cuda, "is_available", lambda: False)
    cuda_only = dataclasses.replace(variant(), devices=("cuda",))

    with pytest.raises(ProtocolError) as error:
        engine.resolve_device("auto", cuda_only)
    assert error.value.code == "DEVICE_UNAVAILABLE"


def test_capabilities_include_enabled_implemented_models(tmp_path, monkeypatch):
    monkeypatch.setattr(engine, "load_catalog", lambda _path: (variant(),))
    monkeypatch.setitem(engine.ADAPTERS, "test-nearest", ModelAdapter(lambda _v, _s: Nearest()))
    stdout = io.StringIO()
    request = json.dumps({"protocol_version": 1, "type": "capabilities",
                          "request_id": "00000000-0000-4000-8000-000000000001"})
    run_worker(io.StringIO(request + "\n"), stdout, io.StringIO(), tmp_path)
    assert json.loads(stdout.getvalue())["models"] == [
        {"id": "test-model", "scales": [2], "devices": ["cpu"]}
    ]


def test_checkpoint_path_matches_digest_keyed_app_cache(tmp_path, monkeypatch):
    model = variant()
    expected = tmp_path / model.sha256 / model.weight_file
    seen = []
    monkeypatch.setattr(engine, "validate_checkpoint", lambda _variant, path: seen.append(path))
    with pytest.raises(ProtocolError):
        engine._load_state_dict(model, tmp_path)
    assert seen == [expected]


def test_catalog_path_can_be_separate_from_weight_cache(tmp_path, monkeypatch):
    catalog = tmp_path / "installed" / "models.json"
    monkeypatch.setenv("DETAILFRAME_CATALOG_PATH", str(catalog))
    seen = []
    monkeypatch.setattr(engine, "load_catalog", lambda path: seen.append(path) or ())
    engine.available_devices(tmp_path / "cache")
    assert seen == [catalog]
