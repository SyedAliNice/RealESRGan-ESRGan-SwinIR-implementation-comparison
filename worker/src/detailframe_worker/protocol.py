import json
from dataclasses import dataclass
from pathlib import Path, PureWindowsPath
from typing import Any
from uuid import UUID

PROTOCOL_VERSION = 1


@dataclass(frozen=True)
class CapabilitiesRequest:
    request_id: UUID


@dataclass(frozen=True)
class EnhanceRequest:
    job_id: UUID
    model_id: str
    scale: int
    device: str
    input_path: str
    output_path: str
    output_format: str


@dataclass(frozen=True)
class CancelRequest:
    job_id: UUID


ProtocolMessage = CapabilitiesRequest | EnhanceRequest | CancelRequest


class ProtocolError(Exception):
    def __init__(
        self,
        code: str,
        message: str,
        *,
        request_id: UUID | None = None,
        job_id: UUID | None = None,
    ) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.request_id = request_id
        self.job_id = job_id


def _identifier(value: Any, name: str) -> UUID:
    if value is None:
        raise ProtocolError("INVALID_REQUEST", f"{name} is required")
    if not isinstance(value, str):
        raise ProtocolError("INVALID_UUID", f"{name} must be a UUID")
    try:
        return UUID(value)
    except (ValueError, AttributeError):
        raise ProtocolError("INVALID_UUID", f"{name} must be a UUID") from None


def _absolute_path(value: Any, name: str) -> str:
    if (
        not isinstance(value, str)
        or not value
        or not (Path(value).is_absolute() or PureWindowsPath(value).is_absolute())
    ):
        raise ProtocolError("INVALID_REQUEST", f"{name} must be an absolute path")
    return value


def _required_string(data: dict[str, Any], key: str) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise ProtocolError("INVALID_REQUEST", f"{key} must be a non-empty string")
    return value


def decode_message(line: str) -> ProtocolMessage:
    try:
        data = json.loads(line)
    except (json.JSONDecodeError, TypeError):
        raise ProtocolError("INVALID_JSON", "Message must be valid JSON") from None
    if not isinstance(data, dict):
        raise ProtocolError("INVALID_REQUEST", "Message must be a JSON object")

    if data.get("protocol_version") != PROTOCOL_VERSION or isinstance(
        data.get("protocol_version"), bool
    ):
        error = ProtocolError("UNSUPPORTED_VERSION", "Unsupported protocol version")
        if data.get("type") in ("enhance", "cancel"):
            try:
                error.job_id = _identifier(data.get("job_id"), "job_id")
            except ProtocolError:
                pass
        elif data.get("type") == "capabilities":
            try:
                error.request_id = _identifier(data.get("request_id"), "request_id")
            except ProtocolError:
                pass
        raise error

    # Extension fields are ignored; behavior-changing additions require a version bump.
    message_type = data.get("type")
    if message_type == "capabilities":
        identifier = _identifier(data.get("request_id"), "request_id")
        return CapabilitiesRequest(identifier)
    if message_type == "cancel":
        identifier = _identifier(data.get("job_id"), "job_id")
        return CancelRequest(identifier)
    if message_type == "enhance":
        job_id = _identifier(data.get("job_id"), "job_id")
        scale = data.get("scale")
        if isinstance(scale, bool) or not isinstance(scale, int) or scale <= 0:
            raise ProtocolError(
                "INVALID_REQUEST", "scale must be a positive integer", job_id=job_id
            )
        try:
            return EnhanceRequest(
                job_id,
                _required_string(data, "model_id"),
                scale,
                _required_string(data, "device"),
                _absolute_path(data.get("input_path"), "input_path"),
                _absolute_path(data.get("output_path"), "output_path"),
                _required_string(data, "output_format"),
            )
        except ProtocolError as error:
            error.job_id = job_id
            raise
    raise ProtocolError("UNKNOWN_TYPE", "Unknown message type")


def encode_message(message: dict[str, Any]) -> str:
    def convert(value: Any) -> Any:
        if isinstance(value, UUID):
            return str(value)
        raise TypeError(f"Object of type {type(value).__name__} is not JSON serializable")

    return json.dumps(message, separators=(",", ":"), default=convert) + "\n"
