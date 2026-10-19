import io
import json

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
