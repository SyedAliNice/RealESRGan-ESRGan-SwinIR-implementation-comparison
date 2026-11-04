import hashlib
import ipaddress
import json
import re
import sys
from dataclasses import dataclass
from importlib import resources
from pathlib import Path
from typing import Any, BinaryIO
from urllib.parse import urlsplit

from jsonschema import Draft202012Validator, FormatChecker
from jsonschema.exceptions import SchemaError, ValidationError


class CatalogValidationError(ValueError):
    pass


class CheckpointValidationError(ValueError):
    pass


class TrustDataError(CatalogValidationError):
    pass


def _trust_path() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent.parent / "shared" / "reviewed-manifests.json"
    return Path(__file__).resolve().parents[3] / "shared" / "reviewed-manifests.json"


def _load_reviewed_manifests() -> dict[str, str]:
    path = _trust_path()

    def unique_object(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"duplicate key: {key}")
            result[key] = value
        return result

    try:
        data = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
        if (
            not isinstance(data, dict)
            or set(data) != {"schema_version", "manifests"}
            or type(data["schema_version"]) is not int
            or data["schema_version"] != 1
            or not isinstance(data["manifests"], dict)
        ):
            raise ValueError("unsupported format")
        for digest, model_id in data["manifests"].items():
            if (
                re.fullmatch(r"[0-9a-f]{64}", digest) is None
                or not isinstance(model_id, str)
                or re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", model_id) is None
            ):
                raise ValueError("invalid digest or model ID")
        return data["manifests"]
    except (OSError, UnicodeError, ValueError) as error:
        raise TrustDataError(f"invalid or unreadable reviewed manifest trust data: {path}: {error}") from error


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
    architecture_config_json: str
    architecture_config_sha256: str
    validation_manifest_sha256: str
    validation_manifest_json: str


def _is_https_url(value: str) -> bool:
    if not isinstance(value, str) or not value:
        return False
    if any(
        character == "\\" or character.isspace() or ord(character) < 0x20 or ord(character) == 0x7F
        for character in value
    ):
        return False

    try:
        parsed = urlsplit(value)
        port = parsed.port
        if (
            parsed.scheme.lower() != "https"
            or not parsed.netloc
            or parsed.username is not None
            or parsed.password is not None
            or "@" in parsed.netloc
            or "\\" in parsed.netloc
            or not parsed.path
            or parsed.path == "/"
            or (port is not None and not 1 <= port <= 65535)
        ):
            return False

        hostname = parsed.hostname
        if not hostname or "%" in hostname:
            return False

        if parsed.netloc.startswith("["):
            closing_bracket = parsed.netloc.find("]")
            if closing_bracket < 0:
                return False
            port_suffix = parsed.netloc[closing_bracket + 1 :]
            if port_suffix and re.fullmatch(r":[0-9]+", port_suffix) is None:
                return False
            ipaddress.IPv6Address(hostname)
            return True

        if "[" in parsed.netloc or "]" in parsed.netloc or ":" in hostname:
            return False
        if parsed.netloc.count(":") > 1:
            return False
        if ":" in parsed.netloc:
            raw_port = parsed.netloc.rsplit(":", 1)[1]
            if not raw_port.isascii() or not raw_port.isdigit():
                return False

        ascii_hostname = hostname.encode("idna").decode("ascii").lower()
        if ascii_hostname.endswith("."):
            ascii_hostname = ascii_hostname[:-1]
        if not ascii_hostname or len(ascii_hostname) > 253:
            return False

        labels = ascii_hostname.split(".")
        for label in labels:
            if (
                not label
                or len(label) > 63
                or not label[0].isalnum()
                or not label[-1].isalnum()
                or any(
                    not (
                        character.isascii()
                        and (character.isalnum() or character == "-")
                    )
                    for character in label
                )
            ):
                return False
            if label.startswith("xn--"):
                decoded_label = label.encode("ascii").decode("idna")
                if decoded_label.encode("idna").decode("ascii").lower() != label:
                    return False

        if all(character.isdigit() or character == "." for character in ascii_hostname):
            ipaddress.IPv4Address(ascii_hostname)
        return True
    except (UnicodeError, ValueError):
        return False


def _format_checker() -> FormatChecker:
    checker = FormatChecker()
    checker.checks("https-url")(_is_https_url)
    return checker


def _read_json(path: Path, description: str) -> Any:
    try:
        with path.open("r", encoding="utf-8") as file:
            return json.load(file)
    except (OSError, json.JSONDecodeError) as error:
        raise CatalogValidationError(f"could not read {description}: {path}") from error


def _read_packaged_json(resource_name: str, description: str) -> Any:
    try:
        resource = resources.files(__package__).joinpath("data", resource_name)
        return json.loads(resource.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise CatalogValidationError(
            f"could not read packaged {description}: {resource_name}"
        ) from error


def _read_json_bytes(path: Path, description: str) -> tuple[bytes, Any]:
    try:
        raw = path.read_bytes()
        return raw, json.loads(raw)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise CatalogValidationError(f"could not read {description}: {path}") from error


def _validation_error(description: str, error: ValidationError) -> CatalogValidationError:
    location = ".".join(str(part) for part in error.absolute_path)
    field = f" at {location}" if location else ""
    return CatalogValidationError(f"invalid {description}{field}: {error.message}")


def _validate_document(schema: Any, document: Any, description: str) -> None:
    try:
        validator = Draft202012Validator(schema, format_checker=_format_checker())
        validator.check_schema(schema)
        validator.validate(document)
    except SchemaError as error:
        raise CatalogValidationError(f"invalid {description} schema: {error.message}") from error
    except ValidationError as error:
        raise _validation_error(description, error) from error


def _architecture_config_digest(manifest: dict[str, Any]) -> str:
    payload = {
        "architecture": manifest["architecture"],
        "scale": manifest["scale"],
        "config": manifest["architecture_config"],
    }
    canonical = json.dumps(
        payload,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
    ).encode("utf-8")
    return hashlib.sha256(canonical).hexdigest()


def _read_reviewed_manifest(
    path: Path,
    model_id: str,
    schema: Any,
    reviewed: dict[str, str],
) -> tuple[bytes, dict[str, Any], str]:
    raw, manifest = _read_json_bytes(path, "validation manifest")
    if not isinstance(manifest, dict):
        raise CatalogValidationError("validation manifest must be an object")
    manifest_digest = hashlib.sha256(raw).hexdigest()
    if reviewed.get(manifest_digest) != model_id:
        raise CatalogValidationError(
            f"validation manifest for {model_id} has not been independently reviewed"
        )

    _validate_document(schema, manifest, "validation manifest")
    if manifest["model_id"] != model_id:
        raise CatalogValidationError("validation manifest model id does not match catalog")
    if manifest["architecture_config_sha256"] != _architecture_config_digest(manifest):
        raise CatalogValidationError(
            "validation manifest architecture/configuration digest does not match"
        )
    return raw, manifest, manifest_digest


def _assert_manifest_matches_variant(variant: ModelVariant) -> None:
    raw = variant.validation_manifest_json.encode("utf-8")
    digest = hashlib.sha256(raw).hexdigest()
    if (
        digest != variant.validation_manifest_sha256
        or _load_reviewed_manifests().get(digest) != variant.id
    ):
        raise CatalogValidationError("model variant does not have a trusted validation manifest")

    try:
        manifest = json.loads(raw)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise CatalogValidationError("model variant validation manifest is invalid") from error
    if (
        not isinstance(manifest, dict)
        or manifest.get("model_id") != variant.id
        or manifest.get("checkpoint_sha256") != variant.sha256
        or manifest.get("architecture") != variant.architecture
        or manifest.get("scale") != variant.scale
        or manifest.get("weight_url") != variant.weight_url
        or manifest.get("license") != variant.license
        or manifest.get("provenance_url") != variant.provenance_url
        or manifest.get("architecture_config_sha256")
        != variant.architecture_config_sha256
        or manifest.get("architecture_config_sha256")
        != _architecture_config_digest(manifest)
        or manifest.get("architecture_config")
        != json.loads(variant.architecture_config_json)
        or tuple(
            (key, tuple(shape))
            for key, shape in sorted(manifest.get("expected_state_dict", {}).items())
        )
        != variant.expected_state_dict
        or set(variant.devices) != set(manifest.get("device_validation", {}))
        or any(
            manifest["device_validation"][device]["checkpoint_sha256"] != variant.sha256
            for device in variant.devices
        )
    ):
        raise CatalogValidationError("model variant does not match its reviewed validation manifest")


def load_catalog(path: Path) -> tuple[ModelVariant, ...]:
    reviewed = _load_reviewed_manifests()
    catalog = _read_json(path, "model catalog")
    schema = _read_json(path.with_name("models.schema.json"), "model catalog schema")
    _validate_document(schema, catalog, "model catalog")

    manifest_schema = _read_packaged_json(
        "validation-manifest.schema.json",
        "validation manifest schema",
    )
    variants = []
    seen_ids = set()
    for entry in catalog["models"]:
        if entry["id"] in seen_ids:
            raise CatalogValidationError(f"duplicate model id: {entry['id']}")
        seen_ids.add(entry["id"])

        validation: tuple[tuple[str, DeviceValidation], ...] = ()
        expected_state_dict: tuple[tuple[str, tuple[int, ...]], ...] = ()
        architecture_config_json = "{}"
        architecture_config_sha256 = ""
        manifest_digest = ""
        manifest_json = ""
        if entry["enabled"]:
            manifest_path = path.parent / entry["validation_manifest"]
            raw, manifest, manifest_digest = _read_reviewed_manifest(
                manifest_path,
                entry["id"],
                manifest_schema,
                reviewed,
            )
            if any(
                manifest[manifest_field] != entry[catalog_field]
                for manifest_field, catalog_field in (
                    ("checkpoint_sha256", "sha256"),
                    ("weight_url", "weight_url"),
                    ("license", "license"),
                    ("provenance_url", "provenance_url"),
                    ("architecture", "architecture"),
                    ("scale", "scale"),
                )
            ):
                raise CatalogValidationError(
                    f"enabled model {entry['id']} does not match its validation manifest"
                )
            devices = tuple(entry["devices"])
            device_validation = manifest["device_validation"]
            if set(devices) != set(device_validation):
                raise CatalogValidationError(
                    f"enabled model {entry['id']} device validation does not match advertised devices"
                )
            if any(
                device_validation[device]["checkpoint_sha256"] != entry["sha256"]
                for device in devices
            ):
                raise CatalogValidationError(
                    f"enabled model {entry['id']} has validation for a different checkpoint"
                )
            validation = tuple(
                (
                    device,
                    DeviceValidation(
                        status=record["status"],
                        tested_at=record["tested_at"],
                        torch_version=record["torch_version"],
                        checkpoint_sha256=record["checkpoint_sha256"],
                    ),
                )
                for device, record in sorted(device_validation.items())
            )
            expected_state_dict = tuple(
                (key, tuple(shape))
                for key, shape in sorted(manifest["expected_state_dict"].items())
            )
            architecture_config_json = json.dumps(
                manifest["architecture_config"],
                sort_keys=True,
                separators=(",", ":"),
                ensure_ascii=False,
            )
            architecture_config_sha256 = manifest["architecture_config_sha256"]
            manifest_json = raw.decode("utf-8")

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
                devices=tuple(entry["devices"]),
                enabled=entry["enabled"],
                validation=validation,
                expected_state_dict=expected_state_dict,
                architecture_config_json=architecture_config_json,
                architecture_config_sha256=architecture_config_sha256,
                validation_manifest_sha256=manifest_digest,
                validation_manifest_json=manifest_json,
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
        _assert_manifest_matches_variant(variant)
    except TrustDataError:
        raise
    except (CatalogValidationError, KeyError, TypeError, ValueError) as error:
        raise CheckpointValidationError(
            "model variant does not have a valid reviewed architecture manifest"
        ) from error

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
