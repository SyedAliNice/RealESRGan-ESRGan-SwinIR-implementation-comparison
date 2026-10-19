import io
import json
import queue
import threading

from detailframe_worker import worker
from detailframe_worker.worker import run_worker


REQUEST_ID = "00000000-0000-4000-8000-000000000001"
JOB_ID = "00000000-0000-4000-8000-000000000002"


def read_events(stdout):
    return [json.loads(line) for line in stdout.getvalue().splitlines()]


def test_capabilities_handshake_preserves_request_id_and_advertises_cpu(tmp_path):
    stdin = io.StringIO(
        json.dumps(
            {"protocol_version": 1, "type": "capabilities", "request_id": REQUEST_ID}
        )
        + "\n"
    )
    stdout = io.StringIO()
    stderr = io.StringIO()

    result = run_worker(stdin, stdout, stderr, tmp_path)

    assert result == 0
    assert read_events(stdout) == [
        {
            "protocol_version": 1,
            "type": "capabilities_result",
            "request_id": REQUEST_ID,
            "worker_version": "1.0.0",
            "models": [],
            "devices": ["cpu"],
        }
    ]
    assert stderr.getvalue() == ""


def test_invalid_enhance_emits_one_correlated_terminal_failure(tmp_path):
    stdin = io.StringIO(
        json.dumps(
            {
                "protocol_version": 1,
                "type": "enhance",
                "job_id": JOB_ID,
                "model_id": "missing",
                "scale": 4,
                "device": "cpu",
                "input_path": "C:\\images\\input.png",
                "output_path": "C:\\images\\output.png",
                "output_format": "png",
            }
        )
        + "\n"
    )
    stdout = io.StringIO()
    stderr = io.StringIO()

    result = run_worker(stdin, stdout, stderr, tmp_path)
    events = read_events(stdout)

    assert result == 0
    assert len(events) == 1
    assert events[0]["type"] == "failure"
    assert events[0]["job_id"] == JOB_ID
    assert events[0]["code"] == "MODEL_UNAVAILABLE"
    assert "error" not in events[0]
    assert stderr.getvalue() == ""


def test_malformed_line_returns_structured_error_and_continues(tmp_path):
    stdin = io.StringIO(
        "{not-json}\n"
        + json.dumps(
            {"protocol_version": 1, "type": "capabilities", "request_id": REQUEST_ID}
        )
        + "\n"
    )
    stdout = io.StringIO()
    stderr = io.StringIO()

    result = run_worker(stdin, stdout, stderr, tmp_path)
    events = read_events(stdout)

    assert result == 0
    assert [event["type"] for event in events] == ["error", "capabilities_result"]
    assert events[0]["code"] == "INVALID_JSON"
    assert events[1]["request_id"] == REQUEST_ID
    assert stderr.getvalue() == ""


def test_invalid_enhance_request_preserves_job_id(tmp_path):
    stdin = io.StringIO(
        json.dumps(
            {
                "protocol_version": 1,
                "type": "enhance",
                "job_id": JOB_ID,
                "model_id": "",
                "scale": 4,
                "device": "cpu",
                "input_path": "C:\\images\\input.png",
                "output_path": "C:\\images\\output.png",
                "output_format": "png",
            }
        )
        + "\n"
    )
    stdout = io.StringIO()
    stderr = io.StringIO()

    run_worker(stdin, stdout, stderr, tmp_path)
    events = read_events(stdout)

    assert len(events) == 1
    assert events[0]["type"] == "error"
    assert events[0]["job_id"] == JOB_ID
    assert events[0]["code"] == "INVALID_REQUEST"
    assert stderr.getvalue() == ""


def test_unsupported_version_enhance_preserves_job_id(tmp_path):
    stdin = io.StringIO(
        json.dumps(
            {
                "protocol_version": 2,
                "type": "enhance",
                "job_id": JOB_ID,
            }
        )
        + "\n"
    )
    stdout = io.StringIO()
    stderr = io.StringIO()

    result = run_worker(stdin, stdout, stderr, tmp_path)
    events = read_events(stdout)

    assert result == 0
    assert len(events) == 1
    assert events[0]["code"] == "UNSUPPORTED_VERSION"
    assert events[0]["job_id"] == JOB_ID


def test_cancel_reaches_active_job_and_emits_one_cancelled_terminal(
    monkeypatch, tmp_path
):
    started = threading.Event()

    def wait_for_cancellation(request, models_dir, cancellation_requested):
        del models_dir
        assert str(request.job_id) == JOB_ID
        started.set()
        assert cancellation_requested.wait(timeout=2)
        return {
            "protocol_version": 1,
            "type": "cancelled",
            "job_id": request.job_id,
        }

    monkeypatch.setattr(worker, "_execute_enhance", wait_for_cancellation)
    stdin = io.StringIO(
        json.dumps(
            {
                "protocol_version": 1,
                "type": "enhance",
                "job_id": JOB_ID,
                "model_id": "model-id",
                "scale": 4,
                "device": "cpu",
                "input_path": "C:\\images\\input.png",
                "output_path": "C:\\images\\output.png",
                "output_format": "png",
            }
        )
        + "\n"
        + json.dumps(
            {"protocol_version": 1, "type": "cancel", "job_id": JOB_ID}
        )
        + "\n"
    )
    stdout = io.StringIO()
    stderr = io.StringIO()

    result = run_worker(stdin, stdout, stderr, tmp_path)
    events = read_events(stdout)

    assert started.is_set()
    assert result == 0
    assert [event["type"] for event in events] == ["cancelled"]
    assert events[0]["job_id"] == JOB_ID
    assert stderr.getvalue() == ""


def test_job_completion_is_emitted_before_input_closes(monkeypatch, tmp_path):
    started = threading.Event()
    finish_job = threading.Event()

    def wait_for_completion(request, models_dir, cancellation_requested):
        del models_dir, cancellation_requested
        started.set()
        assert finish_job.wait(timeout=2)
        return {
            "protocol_version": 1,
            "type": "complete",
            "job_id": request.job_id,
            "output_path": "C:\\images\\output.png",
            "width": 1,
            "height": 1,
        }

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
            self.wrote_event = threading.Event()

        def write(self, value):
            result = super().write(value)
            self.wrote_event.set()
            return result

    monkeypatch.setattr(worker, "_execute_enhance", wait_for_completion)
    stdin = OpenInput()
    stdout = SignallingOutput()
    stderr = io.StringIO()
    result = []

    worker_thread = threading.Thread(
        target=lambda: result.append(run_worker(stdin, stdout, stderr, tmp_path))
    )
    worker_thread.start()
    stdin.lines.put(
        json.dumps(
            {
                "protocol_version": 1,
                "type": "enhance",
                "job_id": JOB_ID,
                "model_id": "model-id",
                "scale": 4,
                "device": "cpu",
                "input_path": "C:\\images\\input.png",
                "output_path": "C:\\images\\output.png",
                "output_format": "png",
            }
        )
        + "\n"
    )

    assert started.wait(timeout=2)
    finish_job.set()
    assert stdout.wrote_event.wait(timeout=2)
    assert [event["type"] for event in read_events(stdout)] == ["complete"]
    assert worker_thread.is_alive()

    stdin.lines.put(None)
    worker_thread.join(timeout=2)

    assert not worker_thread.is_alive()
    assert result == [0]
    assert stderr.getvalue() == ""


def test_unexpected_job_exception_emits_failure_and_stderr_diagnostic(
    monkeypatch, tmp_path
):
    def fail_unexpectedly(request, models_dir, cancellation_requested):
        del request, models_dir, cancellation_requested
        raise RuntimeError("internal inference detail")

    monkeypatch.setattr(worker, "_execute_enhance", fail_unexpectedly)
    stdin = io.StringIO(
        json.dumps(
            {
                "protocol_version": 1,
                "type": "enhance",
                "job_id": JOB_ID,
                "model_id": "model-id",
                "scale": 4,
                "device": "cpu",
                "input_path": "C:\\images\\input.png",
                "output_path": "C:\\images\\output.png",
                "output_format": "png",
            }
        )
        + "\n"
    )
    stdout = io.StringIO()
    stderr = io.StringIO()

    result = run_worker(stdin, stdout, stderr, tmp_path)
    events = read_events(stdout)

    assert result == 0
    assert len(events) == 1
    assert events[0]["type"] == "failure"
    assert events[0]["job_id"] == JOB_ID
    assert events[0]["code"] == "WORKER_ERROR"
    assert "internal inference detail" not in events[0]["message"]
    assert "RuntimeError: internal inference detail" in stderr.getvalue()
    assert "internal inference detail" not in stdout.getvalue()


def test_unreadable_protocol_stream_returns_nonzero_and_stderr_only(tmp_path):
    class BrokenInput(io.StringIO):
        def readline(self, *args):
            raise OSError("stream disconnected")

    stdout = io.StringIO()
    stderr = io.StringIO()

    result = run_worker(BrokenInput(), stdout, stderr, tmp_path)

    assert result != 0
    assert stdout.getvalue() == ""
    assert "stream disconnected" in stderr.getvalue()
