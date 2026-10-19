import io
import json
import queue
import shutil
import threading
from pathlib import Path

import numpy as np
import torch
from PIL import Image

from detailframe_worker import engine
from detailframe_worker import worker as worker_module
from detailframe_worker.catalog import ModelVariant
from detailframe_worker.worker import run_worker

JOB_ID = "00000000-0000-4000-8000-000000000002"
ROOT = Path(__file__).resolve().parents[2]


class OpenInput:
    def __init__(self):
        self.lines = queue.Queue()

    def __iter__(self):
        return self

    def __next__(self):
        line = self.lines.get()
        if line is None:
            raise StopIteration
        return line


class SignallingOutput(io.StringIO):
    def __init__(self):
        super().__init__()
        self.changed = threading.Event()

    def write(self, value):
        result = super().write(value)
        self.changed.set()
        return result


class NearestUpscaler(torch.nn.Module):
    def __init__(self, entered=None, release=None):
        super().__init__()
        self.entered = entered
        self.release = release

    def forward(self, tensor):
        if self.entered is not None:
            self.entered.set()
        if self.release is not None:
            assert self.release.wait(timeout=3)
        return torch.nn.functional.interpolate(tensor, scale_factor=2, mode="nearest")


def test_worker_emits_correlated_progress_and_completes_only_after_output_is_verified(
    monkeypatch, tmp_path
):
    source = tmp_path / "source.png"
    pixels = np.arange(4 * 5 * 3, dtype=np.uint8).reshape(4, 5, 3)
    Image.fromarray(pixels).save(source)
    output = tmp_path / "result.png"
    variant = _test_variant()
    monkeypatch.setattr(engine, "load_catalog", lambda _path: (variant,))
    monkeypatch.setattr(engine, "_load_state_dict", lambda _variant, _directory: {})
    monkeypatch.setitem(
        engine.ADAPTERS, "test-nearest", engine.ModelAdapter(lambda _variant, _state: NearestUpscaler())
    )

    stdin = OpenInput()
    stdout = SignallingOutput()
    stderr = io.StringIO()
    result = []
    worker_thread = threading.Thread(
        target=lambda: result.append(run_worker(stdin, stdout, stderr, tmp_path))
    )
    worker_thread.start()
    stdin.lines.put(_request(source, output))

    assert _wait_for_event(stdout, "complete")
    with Image.open(output) as enhanced:
        assert enhanced.size == (10, 8)
        assert np.array_equal(np.asarray(enhanced), pixels.repeat(2, 0).repeat(2, 1))
    events = _events(stdout)
    progress = [event for event in events if event["type"] == "progress"]
    terminal = [event for event in events if event["type"] in {"complete", "failure", "cancelled"}]
    assert progress
    assert all(event["job_id"] == JOB_ID for event in progress)
    assert len(terminal) == 1
    assert terminal[0]["type"] == "complete"
    assert terminal[0]["job_id"] == JOB_ID
    stdin.lines.put(None)
    worker_thread.join(timeout=2)
    assert not worker_thread.is_alive()
    assert result == [0]
    assert stderr.getvalue() == ""


def test_worker_cancellation_is_terminal_and_removes_partial_output(monkeypatch, tmp_path):
    source = tmp_path / "source.png"
    Image.new("RGB", (8, 8), "red").save(source)
    output = tmp_path / "result.png"
    entered = threading.Event()
    release = threading.Event()
    active_created = threading.Event()
    active_job = []
    original_start_job = worker_module._start_job

    def record_active_job(*args, **kwargs):
        job = original_start_job(*args, **kwargs)
        active_job.append(job)
        active_created.set()
        return job

    monkeypatch.setattr(worker_module, "_start_job", record_active_job)
    monkeypatch.setattr(engine, "load_catalog", lambda _path: (_test_variant(),))
    monkeypatch.setattr(engine, "_load_state_dict", lambda _variant, _directory: {})
    monkeypatch.setitem(
        engine.ADAPTERS,
        "test-nearest",
        engine.ModelAdapter(lambda _variant, _state: NearestUpscaler(entered, release)),
    )

    stdin = OpenInput()
    stdout = SignallingOutput()
    stderr = io.StringIO()
    result = []
    worker_thread = threading.Thread(
        target=lambda: result.append(run_worker(stdin, stdout, stderr, tmp_path))
    )
    worker_thread.start()
    stdin.lines.put(_request(source, output))
    assert entered.wait(timeout=2)
    assert active_created.wait(timeout=2)
    stdin.lines.put(
        json.dumps({"protocol_version": 1, "type": "cancel", "job_id": JOB_ID}) + "\n"
    )
    assert active_job[0].cancellation_requested.wait(timeout=2)
    release.set()
    assert _wait_for_event(stdout, "cancelled")
    events = _events(stdout)
    terminal = [event for event in events if event["type"] in {"complete", "failure", "cancelled"}]
    assert len(terminal) == 1
    assert terminal[0]["type"] == "cancelled"
    assert terminal[0]["job_id"] == JOB_ID
    assert not output.exists()
    assert not list(tmp_path.glob(".*.tmp"))
    stdin.lines.put(None)
    worker_thread.join(timeout=2)
    assert not worker_thread.is_alive()
    assert result == [0]
    assert stderr.getvalue() == ""


def test_empty_production_catalog_fails_without_output(tmp_path):
    models_dir = tmp_path / "models"
    models_dir.mkdir()
    shutil.copyfile(ROOT / "shared" / "models.json", models_dir / "models.json")
    shutil.copyfile(ROOT / "shared" / "models.schema.json", models_dir / "models.schema.json")
    source = tmp_path / "source.png"
    output = tmp_path / "result.png"
    Image.new("RGB", (2, 2), "green").save(source)

    stdout = SignallingOutput()
    stderr = io.StringIO()
    result = run_worker(
        io.StringIO(_request(source, output)),
        stdout,
        stderr,
        models_dir,
    )

    events = _events(stdout)
    assert result == 0
    assert len(events) == 1
    assert events[0]["type"] == "failure"
    assert events[0]["job_id"] == JOB_ID
    assert events[0]["code"] == "MODEL_UNAVAILABLE"
    assert not output.exists()
    assert stderr.getvalue() == ""


def _test_variant():
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


def _request(source: Path, output: Path) -> str:
    return (
        json.dumps(
            {
                "protocol_version": 1,
                "type": "enhance",
                "job_id": JOB_ID,
                "model_id": "test-model",
                "scale": 2,
                "device": "cpu",
                "input_path": str(source),
                "output_path": str(output),
                "output_format": "png",
            }
        )
        + "\n"
    )


def _events(stdout: SignallingOutput):
    return [json.loads(line) for line in stdout.getvalue().splitlines()]


def _wait_for_event(stdout: SignallingOutput, expected: str) -> bool:
    for _ in range(200):
        if any(event["type"] == expected for event in _events(stdout)):
            return True
        stdout.changed.clear()
        stdout.changed.wait(timeout=0.01)
    return False
