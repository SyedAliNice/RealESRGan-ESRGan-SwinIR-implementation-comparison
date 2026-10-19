import hashlib
import io
import json
import os
import tempfile
import threading
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import torch
from PIL import Image, UnidentifiedImageError

from detailframe_worker.catalog import (
    CheckpointValidationError,
    ModelVariant,
    load_catalog,
    validate_checkpoint,
)
from detailframe_worker.images import ImageInfo, MAX_IMAGE_PIXELS, validate_image
from detailframe_worker.protocol import ProtocolError

TILE_SIZE = 512
TILE_PADDING = 16
OUTPUT_FORMATS = {"png": "PNG", "jpeg": "JPEG", "jpg": "JPEG", "webp": "WEBP"}


class EnhanceCancelled(Exception):
    pass


class CancellationToken:
    def __init__(self) -> None:
        self._event = threading.Event()
        self._commit_lock = threading.Lock()

    def __call__(self) -> bool:
        return self.is_set()

    def is_set(self) -> bool:
        return self._event.is_set()

    def set(self) -> None:
        with self._commit_lock:
            self._event.set()

    def wait(self, timeout: float | None = None) -> bool:
        return self._event.wait(timeout)

    def publish(self, publish: Callable[[], None]) -> None:
        with self._commit_lock:
            _check_cancelled(self)
            publish()


@dataclass(frozen=True)
class ModelAdapter:
    build: Callable[[ModelVariant, dict[str, Any]], Any]


# Only architectures with reviewed production implementations may be registered.
ADAPTERS: dict[str, ModelAdapter] = {}


def _load_state_dict(variant: ModelVariant, models_dir: Path) -> dict[str, Any]:
    checkpoint_path = models_dir / variant.weight_file
    try:
        validate_checkpoint(variant, checkpoint_path)
        payload = checkpoint_path.read_bytes()
        if hashlib.sha256(payload).hexdigest() != variant.sha256:
            raise CheckpointValidationError("checkpoint SHA-256 changed during loading")
        if variant.size_bytes is not None and len(payload) != variant.size_bytes:
            raise CheckpointValidationError("checkpoint size changed during loading")
        state_dict = torch.load(io.BytesIO(payload), map_location="cpu", weights_only=True)
    except (OSError, CheckpointValidationError) as error:
        raise ProtocolError("MODEL_UNAVAILABLE", "The selected model checkpoint is unavailable") from error
    except Exception as error:
        raise ProtocolError("MODEL_UNAVAILABLE", "The selected model checkpoint is invalid") from error

    if not isinstance(state_dict, dict) or any(not isinstance(key, str) for key in state_dict):
        raise ProtocolError("MODEL_UNAVAILABLE", "The selected model checkpoint is invalid")
    return state_dict


def _check_cancelled(cancelled: Callable[[], bool]) -> None:
    if cancelled():
        raise EnhanceCancelled()


def _report(
    progress: Callable[[str, float], None], cancelled: Callable[[], bool], stage: str, value: float
) -> None:
    _check_cancelled(cancelled)
    progress(stage, value)


def _run_tiles(
    model: Any,
    source: torch.Tensor,
    scale: int,
    progress: Callable[[str, float], None],
    cancelled: Callable[[], bool],
) -> torch.Tensor:
    _, channels, height, width = source.shape
    output = torch.empty((1, channels, height * scale, width * scale), dtype=torch.float32)
    tile_count = ((height + TILE_SIZE - 1) // TILE_SIZE) * (
        (width + TILE_SIZE - 1) // TILE_SIZE
    )
    completed = 0

    for top in range(0, height, TILE_SIZE):
        for left in range(0, width, TILE_SIZE):
            _check_cancelled(cancelled)
            bottom = min(top + TILE_SIZE, height)
            right = min(left + TILE_SIZE, width)
            input_top = max(0, top - TILE_PADDING)
            input_left = max(0, left - TILE_PADDING)
            input_bottom = min(height, bottom + TILE_PADDING)
            input_right = min(width, right + TILE_PADDING)
            tile = source[:, :, input_top:input_bottom, input_left:input_right]
            prediction = model(tile)
            expected_shape = (
                1,
                channels,
                (input_bottom - input_top) * scale,
                (input_right - input_left) * scale,
            )
            if not isinstance(prediction, torch.Tensor) or tuple(prediction.shape) != expected_shape:
                raise ProtocolError("INFERENCE_FAILED", "The model returned an invalid image shape")
            if not torch.isfinite(prediction).all().item():
                raise ProtocolError("INFERENCE_FAILED", "The model returned non-finite pixels")

            crop_top = (top - input_top) * scale
            crop_left = (left - input_left) * scale
            crop_bottom = crop_top + (bottom - top) * scale
            crop_right = crop_left + (right - left) * scale
            output[
                :,
                :,
                top * scale : bottom * scale,
                left * scale : right * scale,
            ] = prediction[:, :, crop_top:crop_bottom, crop_left:crop_right].float()
            completed += 1
            _report(progress, cancelled, "inference", 0.1 + 0.7 * completed / tile_count)
    return output


def _verify_encoded(path: Path, expected_size: tuple[int, int], expected_format: str) -> ImageInfo:
    try:
        with Image.open(path) as image:
            image_format = image.format
            size = image.size
            image.verify()
        with Image.open(path) as image:
            pixels = np.asarray(image.convert("RGBA" if "A" in image.getbands() else "RGB"))
            if not np.isfinite(pixels).all():
                raise ProtocolError("OUTPUT_FAILED", "The encoded image contains invalid pixels")
    except ProtocolError:
        raise
    except (OSError, SyntaxError, ValueError, UnidentifiedImageError):
        raise ProtocolError("OUTPUT_FAILED", "The encoded image could not be reopened") from None
    if size != expected_size or image_format != expected_format:
        raise ProtocolError("OUTPUT_FAILED", "The encoded image does not match the requested output")
    return ImageInfo(width=size[0], height=size[1], format=image_format)


def enhance(
    model_id: str,
    scale: int,
    device: str,
    input_path: Path,
    output_path: Path,
    output_format: str,
    progress: Callable[[str, float], None],
    cancelled: Callable[[], bool],
    *,
    models_dir: Path | None = None,
    max_pixels: int = MAX_IMAGE_PIXELS,
) -> ImageInfo:
    if isinstance(scale, bool) or not isinstance(scale, int) or scale <= 0:
        raise ProtocolError("UNSUPPORTED_SCALE", "The requested scale is not supported")
    if device != "cpu":
        raise ProtocolError("UNSUPPORTED_DEVICE", "Only CPU inference is supported")
    format_name = OUTPUT_FORMATS.get(output_format.lower())
    if format_name is None:
        raise ProtocolError("INVALID_REQUEST", "The requested output format is not supported")
    if input_path.resolve(strict=False) == output_path.resolve(strict=False):
        raise ProtocolError("INVALID_REQUEST", "Input and output paths must be different")

    models_dir = models_dir or input_path.parent
    catalog_path = models_dir / "models.json"
    try:
        catalog = load_catalog(catalog_path)
    except Exception as error:
        raise ProtocolError("MODEL_UNAVAILABLE", "The model catalog could not be loaded") from error
    variant = next((item for item in catalog if item.id == model_id and item.enabled), None)
    if variant is None:
        raise ProtocolError("MODEL_UNAVAILABLE", "The requested model is unavailable")
    if scale != variant.scale:
        raise ProtocolError("UNSUPPORTED_SCALE", "The requested scale is not supported by this model")
    if device not in variant.devices or not variant.expected_state_dict:
        raise ProtocolError("MODEL_UNAVAILABLE", "The requested model is not validated for CPU inference")
    adapter = ADAPTERS.get(variant.architecture)
    if adapter is None:
        raise ProtocolError("MODEL_UNAVAILABLE", "The requested model architecture is unavailable")
    try:
        architecture_config = json.loads(variant.architecture_config_json)
    except (TypeError, json.JSONDecodeError) as error:
        raise ProtocolError("MODEL_UNAVAILABLE", "The requested model configuration is invalid") from error
    if not isinstance(architecture_config, dict):
        raise ProtocolError("MODEL_UNAVAILABLE", "The requested model configuration is invalid")

    _report(progress, cancelled, "loading_model", 0.0)
    state_dict = _load_state_dict(variant, models_dir)
    try:
        model = adapter.build(variant, state_dict)
        if hasattr(model, "eval"):
            model.eval()
    except Exception as error:
        raise ProtocolError("MODEL_UNAVAILABLE", "The selected model could not be initialized") from error
    if not callable(model):
        raise ProtocolError("MODEL_UNAVAILABLE", "The selected model adapter is invalid")
    _report(progress, cancelled, "loading_model", 0.1)

    image_info = validate_image(input_path, max_pixels)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    if output_path.resolve(strict=False) == input_path.resolve(strict=True):
        raise ProtocolError("INVALID_REQUEST", "Input and output paths must be different")

    temporary_path: Path | None = None
    try:
        with Image.open(input_path) as image:
            rgba = image.mode in {"RGBA", "LA"} or "transparency" in image.info
            if rgba:
                rgba_image = image.convert("RGBA")
                alpha = rgba_image.getchannel("A")
                rgb_image = rgba_image.convert("RGB")
            else:
                alpha = None
                rgb_image = image.convert("RGB")
            source_array = np.asarray(rgb_image, dtype=np.float32) / 255.0
        source = torch.from_numpy(source_array).permute(2, 0, 1).unsqueeze(0).contiguous()

        with torch.inference_mode():
            enhanced = _run_tiles(model, source, scale, progress, cancelled)
        enhanced = enhanced[0].clone().clamp_(0.0, 1.0).mul_(255.0).round_().to(torch.uint8)
        pixels = enhanced.permute(1, 2, 0).cpu().numpy()
        result = Image.fromarray(pixels, mode="RGB")
        if alpha is not None:
            alpha = alpha.resize((image_info.width * scale, image_info.height * scale), Image.Resampling.BILINEAR)
            result.putalpha(alpha)
        if format_name == "JPEG" and result.mode != "RGB":
            result = result.convert("RGB")

        _report(progress, cancelled, "encoding", 0.9)
        descriptor, temporary_name = tempfile.mkstemp(
            prefix=f".{output_path.name}.", suffix=".tmp", dir=output_path.parent
        )
        os.close(descriptor)
        temporary_path = Path(temporary_name)
        result.save(temporary_path, format=format_name)
        _verify_encoded(
            temporary_path,
            (image_info.width * scale, image_info.height * scale),
            format_name,
        )
        publish = lambda: os.replace(temporary_path, output_path)
        synchronized_publish = getattr(cancelled, "publish", None)
        if callable(synchronized_publish):
            synchronized_publish(publish)
        else:
            _check_cancelled(cancelled)
            publish()
        temporary_path = None
        progress("encoding", 1.0)
    except EnhanceCancelled:
        raise
    except ProtocolError:
        raise
    except (OSError, ValueError, RuntimeError) as error:
        raise ProtocolError("INFERENCE_FAILED", "Image enhancement failed") from error
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)
    return ImageInfo(
        width=image_info.width * scale,
        height=image_info.height * scale,
        format=format_name,
    )
