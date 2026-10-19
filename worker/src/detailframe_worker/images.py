import warnings
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, UnidentifiedImageError

from detailframe_worker.protocol import ProtocolError

MAX_IMAGE_PIXELS = 100_000_000

_FORMATS_BY_EXTENSION = {
    ".jpeg": "JPEG",
    ".jpg": "JPEG",
    ".png": "PNG",
    ".webp": "WEBP",
}


@dataclass(frozen=True)
class ImageInfo:
    width: int
    height: int
    format: str


def validate_image(path: Path, max_pixels: int) -> ImageInfo:
    if isinstance(max_pixels, bool) or not isinstance(max_pixels, int) or max_pixels <= 0:
        raise ValueError("max_pixels must be a positive integer")

    expected_format = _FORMATS_BY_EXTENSION.get(path.suffix.lower())
    if expected_format is None:
        raise ProtocolError("INVALID_IMAGE", "The input image format is not supported")

    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(path) as image:
                image_format = image.format
                width, height = image.size
                if image_format not in {"JPEG", "PNG", "WEBP"}:
                    raise ProtocolError(
                        "INVALID_IMAGE", "The input image format is not supported"
                    )
                if image_format != expected_format:
                    raise ProtocolError(
                        "INVALID_IMAGE",
                        "The input image extension does not match its contents",
                    )
                if width <= 0 or height <= 0:
                    raise ProtocolError("INVALID_IMAGE", "The input image is invalid")
                if width * height > max_pixels:
                    raise ProtocolError(
                        "IMAGE_TOO_LARGE",
                        "The input image exceeds the maximum pixel count",
                    )
                image.verify()
    except ProtocolError:
        raise
    except (Image.DecompressionBombError, Image.DecompressionBombWarning):
        raise ProtocolError(
            "IMAGE_TOO_LARGE", "The input image exceeds the maximum pixel count"
        ) from None
    except (OSError, SyntaxError, ValueError, UnidentifiedImageError):
        raise ProtocolError("INVALID_IMAGE", "The input image is invalid") from None

    return ImageInfo(width=width, height=height, format=image_format)
