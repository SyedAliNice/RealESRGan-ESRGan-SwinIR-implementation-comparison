import hashlib
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, BinaryIO

from jsonschema import Draft202012Validator, FormatChecker
from jsonschema.exceptions import SchemaError, ValidationError


class CatalogValidationError(ValueError):
    pass


class CheckpointValidationError(ValueError):
    pass


@dataclass(frozen=True)
class DeviceValidation:
    status: str
    tested_at: str
    torch_version: str
    checkpoint_sha256: str


@dataclass(frozen=True)
class ModelVariant:
    id: str
    display_name: str
    architecture: str
    scale: int
    weight_file: str
    weight_url: str
    sha256: str
    license: str
    provenance_url: str
    size_bytes: int | None
    devices: tuple[str, ...]
    enabled: bool
    validation: tuple[tuple[str, DeviceValidation], ...]
    expected_state_dict: tuple[tuple[str, tuple[int, ...]], ...]


def _read_json(path: Path, description: str) -> Any:
    try:
        with path.open("r", encoding="utf-8") as file:
            return json.load(file)
    except (OSError, json.JSONDecodeError) as error:
        raise CatalogValidationError(f"could not read {description}: {path}") from error


def _validation_error(path: Path, error: ValidationError) -> CatalogValidationError:
    location = ".".join(str(part) for part in error.absolute_path)
    field = f" at {location}" if location else ""
    return CatalogValidationError(f"invalid model catalog{field}: {error.message}")


def load_catalog(path: Path) -> tuple[ModelVariant, ...]:
    catalog = _read_json(path, "model catalog")
    schema_path = path.with_name("models.schema.json")
    schema = _read_json(schema_path, "model catalog schema")
    try:
        validator = Draft202012Validator(schema, format_checker=FormatChecker())
        validator.check_schema(schema)
        validator.validate(catalog)
    except SchemaError as error:
        raise CatalogValidationError(f"invalid model catalog schema: {error.message}") from error
    except ValidationError as error:
        raise _validation_error(path, error) from error

    variants = []
    seen_ids = set()
    for entry in catalog["models"]:
        if entry["id"] in seen_ids:
            raise CatalogValidationError(f"duplicate model id: {entry['id']}")
        seen_ids.add(entry["id"])

        validation = entry["validation"]
        devices = tuple(entry["devices"])
        if entry["enabled"]:
            if not set(devices).issubset(validation):
                raise CatalogValidationError(
                    f"enabled model {entry['id']} lacks validation for an advertised device"
                )
            if any(
                validation[device]["checkpoint_sha256"] != entry["sha256"]
                for device in devices
            ):
                raise CatalogValidationError(
                    f"enabled model {entry['id']} has validation for a different checkpoint"
                )

        expected_state_dict = entry.get("expected_state_dict", {})
        variants.append(
            ModelVariant(
                id=entry["id"],
                display_name=entry["display_name"],
                architecture=entry["architecture"],
                scale=entry["scale"],
                weight_file=entry["weight_file"],
                weight_url=entry["weight_url"],
                sha256=entry["sha256"],
                license=entry["license"],
                provenance_url=entry["provenance_url"],
                size_bytes=entry.get("size_bytes"),
                devices=devices,
                enabled=entry["enabled"],
                validation=tuple(
                    (
                        device,
                        DeviceValidation(
                            status=record["status"],
                            tested_at=record["tested_at"],
                            torch_version=record["torch_version"],
                            checkpoint_sha256=record["checkpoint_sha256"],
                        ),
                    )
                    for device, record in sorted(validation.items())
                ),
                expected_state_dict=tuple(
                    (key, tuple(shape))
                    for key, shape in sorted(expected_state_dict.items())
                ),
            )
        )

    return tuple(variants)


def _file_sha256(checkpoint: BinaryIO) -> tuple[str, int]:
    digest = hashlib.sha256()
    size = 0
    while chunk := checkpoint.read(1024 * 1024):
        digest.update(chunk)
        size += len(chunk)
    return digest.hexdigest(), size


def validate_checkpoint(variant: ModelVariant, checkpoint_path: Path) -> None:
    if not variant.enabled:
        raise CheckpointValidationError("model variant is not enabled")
    if checkpoint_path.name != variant.weight_file:
        raise CheckpointValidationError("checkpoint filename does not match the catalog")
    if not variant.expected_state_dict:
        raise CheckpointValidationError("model variant has no expected state-dict specification")

    try:
        checkpoint_file = checkpoint_path.open("rb")
    except OSError as error:
        raise CheckpointValidationError("checkpoint file could not be read") from error

    with checkpoint_file:
        actual_sha256, actual_size = _file_sha256(checkpoint_file)
        if actual_sha256 != variant.sha256:
            raise CheckpointValidationError("checkpoint SHA-256 does not match the catalog")
        if variant.size_bytes is not None and actual_size != variant.size_bytes:
            raise CheckpointValidationError("checkpoint size does not match the catalog")

        checkpoint_file.seek(0)
        import torch

        try:
            state_dict = torch.load(
                checkpoint_file,
                map_location="cpu",
                weights_only=True,
            )
        except Exception as error:
            raise CheckpointValidationError(
                "checkpoint could not be safely deserialized"
            ) from error

    if not isinstance(state_dict, dict) or any(
        not isinstance(key, str) for key in state_dict
    ):
        raise CheckpointValidationError("checkpoint must contain a state dictionary")

    expected = dict(variant.expected_state_dict)
    if set(state_dict) != set(expected):
        raise CheckpointValidationError("checkpoint keys do not match the model architecture")
    for key, expected_shape in expected.items():
        value = state_dict[key]
        if not isinstance(value, torch.Tensor) or tuple(value.shape) != expected_shape:
            raise CheckpointValidationError(
                f"checkpoint tensor {key!r} does not match the model architecture"
            )
