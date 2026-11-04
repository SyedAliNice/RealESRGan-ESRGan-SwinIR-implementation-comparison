import json
import subprocess
import sys
from pathlib import Path

import pytest

REQUEST_ID = "00000000-0000-4000-8000-000000000001"
SRC = Path(__file__).resolve().parents[2] / "src"


@pytest.mark.skipif(sys.platform != "win32", reason="Windows process test")
def test_real_worker_process_speaks_json_lines_on_stdout_only(tmp_path):
    request = json.dumps(
        {"protocol_version": 1, "type": "capabilities", "request_id": REQUEST_ID}
    )
    process = subprocess.run(
        [sys.executable, "-m", "detailframe_worker", "--protocol-stdio", "--models-dir", str(tmp_path)],
        input=request + "\n",
        capture_output=True,
        text=True,
        timeout=60,
        env={**__import__("os").environ, "PYTHONPATH": str(SRC)},
    )

    assert process.returncode == 0
    lines = process.stdout.splitlines()
    assert len(lines) == 1
    event = json.loads(lines[0])
    assert event["type"] == "capabilities_result"
    assert event["request_id"] == REQUEST_ID
    assert event["devices"] == ["cpu"]
    assert process.stderr == ""
