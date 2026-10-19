import sys
from pathlib import Path
from typing import TextIO
from uuid import UUID, uuid4

from detailframe_worker import __version__
from detailframe_worker.protocol import (
    PROTOCOL_VERSION,
    CancelRequest,
    CapabilitiesRequest,
    EnhanceRequest,
    ProtocolError,
    ProtocolMessage,
    decode_message,
    encode_message,
)


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


def _job_failure(job_id: UUID, code: str, message: str) -> dict[str, object]:
    return {
        "protocol_version": PROTOCOL_VERSION,
        "type": "failure",
        "job_id": job_id,
        "code": code,
        "message": message,
    }


def _handle_message(
    message: ProtocolMessage, active_job: UUID | None
) -> tuple[dict[str, object] | None, UUID | None]:
    if isinstance(message, CapabilitiesRequest):
        return (
            {
                "protocol_version": PROTOCOL_VERSION,
                "type": "capabilities_result",
                "request_id": message.request_id,
                "worker_version": __version__,
                "models": [],
                "devices": ["cpu"],
            },
            active_job,
        )
    if isinstance(message, CancelRequest):
        if active_job == message.job_id:
            return (
                {
                    "protocol_version": PROTOCOL_VERSION,
                    "type": "cancelled",
                    "job_id": message.job_id,
                },
                None,
            )
        return (
            _error_event(
                ProtocolError(
                    "JOB_NOT_ACTIVE", "The requested job is not active", job_id=message.job_id
                )
            ),
            active_job,
        )
    if active_job is not None:
        return (
            _error_event(
                ProtocolError(
                    "JOB_ALREADY_ACTIVE",
                    "Another inference job is already active",
                    job_id=message.job_id,
                )
            ),
            active_job,
        )

    # Inference execution is provided by a later worker task; Task 2 establishes
    # the protocol and reports unavailable models as a terminal job failure.
    return (
        _job_failure(message.job_id, "MODEL_UNAVAILABLE", "The requested model is unavailable"),
        None,
    )


def run_worker(stdin: TextIO, stdout: TextIO, stderr: TextIO, models_dir: Path) -> int:
    del models_dir
    active_job: UUID | None = None
    try:
        for line in stdin:
            if not line.strip():
                continue
            try:
                message = decode_message(line)
                event, active_job = _handle_message(message, active_job)
            except ProtocolError as error:
                event = _error_event(error)
            if event is not None:
                stdout.write(encode_message(event))
                stdout.flush()
    except (OSError, UnicodeError) as error:
        print(f"Protocol stream failed: {error}", file=stderr)
        return 1
    return 0
