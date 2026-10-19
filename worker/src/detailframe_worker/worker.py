import queue
import threading
import traceback
from dataclasses import dataclass
from pathlib import Path
from typing import Callable
from typing import TextIO
from uuid import UUID, uuid4

from detailframe_worker import __version__
from detailframe_worker import engine
from detailframe_worker.protocol import (
    PROTOCOL_VERSION,
    CancelRequest,
    CapabilitiesRequest,
    EnhanceRequest,
    ProtocolError,
    decode_message,
    encode_message,
)

SHUTDOWN_TIMEOUT_SECONDS = 1.0


def _error_event(error: ProtocolError) -> dict[str, object]:
    event: dict[str, object] = {
        "protocol_version": PROTOCOL_VERSION,
        "type": "error",
        "code": error.code,
        "message": error.message,
    }
    if error.request_id is not None:
        event["request_id"] = error.request_id
    elif error.job_id is not None:
        event["job_id"] = error.job_id
    else:
        event["request_id"] = uuid4()
    return event


def _safe_stderr(stderr: TextIO, message: str) -> None:
    try:
        print(message, file=stderr)
        stderr.flush()
    except Exception:
        pass


def _job_failure(job_id: UUID, code: str, message: str) -> dict[str, object]:
    return {
        "protocol_version": PROTOCOL_VERSION,
        "type": "failure",
        "job_id": job_id,
        "code": code,
        "message": message,
    }


def _execute_enhance(
    request: EnhanceRequest,
    models_dir: Path,
    cancellation_requested: engine.CancellationToken,
    progress: Callable[[str, float], None],
) -> dict[str, object]:
    if cancellation_requested.is_set():
        return {
            "protocol_version": PROTOCOL_VERSION,
            "type": "cancelled",
            "job_id": request.job_id,
        }
    try:
        info = engine.enhance(
            model_id=request.model_id,
            scale=request.scale,
            device=request.device,
            input_path=Path(request.input_path),
            output_path=Path(request.output_path),
            output_format=request.output_format,
            progress=progress,
            cancelled=cancellation_requested,
            models_dir=models_dir,
        )
    except engine.EnhanceCancelled:
        return {
            "protocol_version": PROTOCOL_VERSION,
            "type": "cancelled",
            "job_id": request.job_id,
        }
    except ProtocolError as error:
        return _job_failure(request.job_id, error.code, error.message)
    return {
        "protocol_version": PROTOCOL_VERSION,
        "type": "complete",
        "job_id": request.job_id,
        "output_path": request.output_path,
        "width": info.width,
        "height": info.height,
    }


@dataclass
class _ActiveJob:
    job_id: UUID
    cancellation_requested: engine.CancellationToken
    thread: threading.Thread


def _start_job(
    request: EnhanceRequest,
    models_dir: Path,
    stderr: TextIO,
    events: queue.Queue[tuple[str, object]],
) -> _ActiveJob:
    cancellation_requested = engine.CancellationToken()

    def execute() -> None:
        try:
            event = _execute_enhance(
                request,
                models_dir,
                cancellation_requested,
                lambda stage, fraction: events.put(
                    ("job_progress", (request.job_id, stage, fraction))
                ),
            )
        except Exception:
            traceback.print_exc(file=stderr)
            event = _job_failure(
                request.job_id,
                "WORKER_ERROR",
                "The worker encountered an unexpected error",
            )
        events.put(("job_result", (request.job_id, event)))

    thread = threading.Thread(
        target=execute, name=f"detailframe-job-{request.job_id}", daemon=True
    )
    active_job = _ActiveJob(request.job_id, cancellation_requested, thread)
    thread.start()
    return active_job


def run_worker(stdin: TextIO, stdout: TextIO, stderr: TextIO, models_dir: Path) -> int:
    active: _ActiveJob | None = None
    events: queue.Queue[tuple[str, object]] = queue.Queue()
    input_error: Exception | None = None
    reached_eof = False

    def emit(event: dict[str, object]) -> None:
        stdout.write(encode_message(event))
        stdout.flush()

    def read_input() -> None:
        try:
            for line in stdin:
                events.put(("line", line))
        except Exception as error:
            events.put(("input_error", error))
        finally:
            events.put(("eof", None))

    def request_cancel_and_wait() -> bool:
        nonlocal active
        if active is None:
            return True
        job = active
        job.cancellation_requested.set()
        job.thread.join(timeout=SHUTDOWN_TIMEOUT_SECONDS)
        if job.thread.is_alive():
            _safe_stderr(
                stderr,
                f"Active job {job.job_id} did not stop within "
                f"{SHUTDOWN_TIMEOUT_SECONDS:g} seconds; abandoning it",
            )
            active = None
            return False
        return True

    def abort_active_job() -> None:
        nonlocal active
        request_cancel_and_wait()
        active = None

    input_thread = threading.Thread(target=read_input, name="detailframe-input", daemon=True)
    input_thread.start()

    try:
        while True:
            kind, payload = events.get()
            if kind == "line":
                line = payload
                if not isinstance(line, str) or not line.strip():
                    continue
                try:
                    message = decode_message(line)
                except ProtocolError as error:
                    emit(_error_event(error))
                    continue

                if isinstance(message, CapabilitiesRequest):
                    emit(
                        {
                            "protocol_version": PROTOCOL_VERSION,
                            "type": "capabilities_result",
                            "request_id": message.request_id,
                            "worker_version": __version__,
                            "models": [],
                            "devices": engine.available_devices(models_dir),
                        }
                    )
                elif isinstance(message, CancelRequest):
                    if active is not None and active.job_id == message.job_id:
                        active.cancellation_requested.set()
                    else:
                        emit(
                            _error_event(
                                ProtocolError(
                                    "JOB_NOT_ACTIVE",
                                    "The requested job is not active",
                                    job_id=message.job_id,
                                )
                            )
                        )
                elif active is not None:
                    emit(
                        _error_event(
                            ProtocolError(
                                "JOB_ALREADY_ACTIVE",
                                "Another inference job is already active",
                                job_id=message.job_id,
                            )
                        )
                    )
                else:
                    active = _start_job(message, models_dir, stderr, events)
            elif kind == "job_result":
                job_id, event = payload
                if active is not None and active.job_id == job_id:
                    active.thread.join()
                    if input_error is not None:
                        emit(
                            _job_failure(
                                active.job_id,
                                "WORKER_ERROR",
                                "The worker stopped unexpectedly",
                            )
                        )
                    else:
                        emit(event)
                    active = None
                    if reached_eof:
                        return 1 if input_error is not None else 0
            elif kind == "job_progress":
                job_id, stage, fraction = payload
                if active is not None and active.job_id == job_id:
                    emit(
                        {
                            "protocol_version": PROTOCOL_VERSION,
                            "type": "progress",
                            "job_id": job_id,
                            "stage": stage,
                            "progress": fraction,
                        }
                    )
            elif kind == "input_error":
                input_error = payload
                _safe_stderr(stderr, f"Protocol stream failed: {input_error}")
                reached_eof = True
                if active is not None:
                    if not request_cancel_and_wait():
                        return 1
                else:
                    return 1
            elif kind == "eof":
                reached_eof = True
                if active is not None:
                    if not request_cancel_and_wait():
                        return 1
                else:
                    return 0
    except (OSError, UnicodeError) as error:
        _safe_stderr(stderr, f"Protocol stream failed: {error}")
        try:
            abort_active_job()
        except Exception:
            traceback.print_exc(file=stderr)
        return 1
    except Exception:
        traceback.print_exc(file=stderr)
        try:
            abort_active_job()
        except Exception:
            traceback.print_exc(file=stderr)
        return 1
