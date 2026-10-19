import io
import json
import queue
import threading
from uuid import UUID

import pytest
import torch
from PIL import Image

from detailframe_worker import engine
from detailframe_worker.catalog import ModelVariant
from detailframe_worker.engine import CancellationToken, EnhanceCancelled, ModelAdapter
from detailframe_worker.protocol import EnhanceRequest
from detailframe_worker.worker import _execute_enhance

JOB_ID = UUID("00000000-0000-4000-8000-000000000006")


class Nearest(torch.nn.Module):
    def forward(self, tensor):
        return torch.nn.functional.interpolate(tensor, scale_factor=2, mode="nearest")


def variant():
    return ModelVariant(
        id="test-model",
        display_name="Test",
        architecture="test-nearest",
        scale=2,
        weight_file="test.pth",
        weight_url="https://example.invalid/test.pth",
        sha256="0" * 64,
        license="test",
        provenance_url="https://example.invalid/test",
        size_bytes=None,
        devices=("cpu",),
        enabled=True,
        validation=(),
        expected_state_dict=(("test.weight", (1,)),),
        architecture_config_json='{"scale":2}',
        architecture_config_sha256="",
        validation_manifest_sha256="",
        validation_manifest_json="",
    )


@pytest.fixture(autouse=True)
def deterministic_model(monkeypatch):
    monkeypatch.setattr(engine, "load_catalog", lambda _path: (variant(),))
    monkeypatch.setattr(engine, "_load_state_dict", lambda _variant, _directory: {})
    monkeypatch.setitem(engine.ADAPTERS, "test-nearest", ModelAdapter(lambda _v, _s: Nearest()))


@pytest.fixture
def paths(tmp_path):
    source = tmp_path / "source.png"
    Image.new("RGB", (8, 8), "red").save(source)
    return source, tmp_path / "out.png"


def run(tmp_path, paths, token, progress=lambda stage, fraction: None):
    source, output = paths
    return engine.enhance(
        "test-model", 2, "cpu", source, output, "png", progress, token, models_dir=tmp_path
    )


def request(paths, tmp_path):
    source, output = paths
    return EnhanceRequest(
        JOB_ID, "test-model", 2, "cpu", str(source), str(output), "png"
    )


def assert_clean(tmp_path, output):
    assert not output.exists()
    assert not list(tmp_path.glob(".*.tmp"))


def test_cancel_before_job_emits_cancelled_without_touching_engine(tmp_path, paths, monkeypatch):
    monkeypatch.setattr(engine, "enhance", lambda **_kw: pytest.fail("must not start"))
    token = CancellationToken()
    token.set()

    event = _execute_enhance(request(paths, tmp_path), tmp_path, token, lambda *_a: None)

    assert event["type"] == "cancelled" and event["job_id"] == JOB_ID
    assert_clean(tmp_path, paths[1])


@pytest.mark.parametrize("stage", ["loading_model", "inference", "encoding"])
def test_cancel_during_each_phase_removes_partial_output(tmp_path, paths, stage):
    token = CancellationToken()

    def progress(name, fraction):
        if name == stage:
            token.set()

    with pytest.raises(EnhanceCancelled):
        run(tmp_path, paths, token, progress)
    assert_clean(tmp_path, paths[1])


def test_cancel_during_run_emits_single_cancelled_terminal_event(tmp_path, paths):
    token = CancellationToken()
    seen = []

    def progress(name, fraction):
        seen.append(name)
        if name == "inference":
            token.set()

    event = _execute_enhance(request(paths, tmp_path), tmp_path, token, progress)

    assert event["type"] == "cancelled" and event["job_id"] == JOB_ID
    assert "encoding" not in seen
    assert_clean(tmp_path, paths[1])


def test_cancel_after_completion_does_not_remove_result(tmp_path, paths):
    token = CancellationToken()
    event = _execute_enhance(request(paths, tmp_path), tmp_path, token, lambda *_a: None)
    token.set()

    assert event["type"] == "complete"
    with Image.open(paths[1]) as result:
        assert result.size == (16, 16)


def test_cancel_keeps_previously_completed_result_untouched(tmp_path, paths):
    _, output = paths
    Image.new("RGB", (3, 3), "blue").save(output)
    before = output.read_bytes()
    token = CancellationToken()

    with pytest.raises(EnhanceCancelled):
        run(tmp_path, paths, token, lambda name, _f: token.set() if name == "encoding" else None)

    assert output.read_bytes() == before
    assert not list(tmp_path.glob(".*.tmp"))


def test_worker_reports_cancel_for_inactive_job_as_error(tmp_path):
    from detailframe_worker.worker import run_worker

    stdout = io.StringIO()
    line = json.dumps({"protocol_version": 1, "type": "cancel", "job_id": str(JOB_ID)}) + "\n"

    assert run_worker(io.StringIO(line), stdout, io.StringIO(), tmp_path) == 0

    events = [json.loads(item) for item in stdout.getvalue().splitlines()]
    assert [e["code"] for e in events] == ["JOB_NOT_ACTIVE"]
