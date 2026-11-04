import pytest
from PIL import Image

from detailframe_worker.images import validate_image
from detailframe_worker.protocol import ProtocolError


def test_pillow_warning_threshold_matches_first_rejected_pixel_count():
    from detailframe_worker.images import pillow_pixel_limit
    with pillow_pixel_limit(100):
        assert Image.MAX_IMAGE_PIXELS == 99


@pytest.mark.parametrize(
    ("format_name", "extension"),
    [("PNG", ".png"), ("JPEG", ".jpg"), ("WEBP", ".webp")],
)
def test_validates_supported_image_format_and_dimensions(tmp_path, format_name, extension):
    path = tmp_path / f"source{extension}"
    Image.new("RGB", (3, 2), color="red").save(path, format=format_name)

    info = validate_image(path, max_pixels=100)

    assert (info.width, info.height, info.format) == (3, 2, format_name)


def test_rejects_corrupt_image_bytes(tmp_path):
    path = tmp_path / "broken.png"
    path.write_bytes(b"not an image")

    with pytest.raises(ProtocolError) as error:
        validate_image(path, max_pixels=100)

    assert error.value.code == "INVALID_IMAGE"


def test_rejects_unsupported_decoded_format(tmp_path):
    path = tmp_path / "unsupported.bmp"
    Image.new("RGB", (2, 2)).save(path, format="BMP")

    with pytest.raises(ProtocolError) as error:
        validate_image(path, max_pixels=100)

    assert error.value.code == "INVALID_IMAGE"


def test_rejects_extension_that_disagrees_with_decoded_format(tmp_path):
    path = tmp_path / "misleading.jpg"
    Image.new("RGB", (2, 2)).save(path, format="PNG")

    with pytest.raises(ProtocolError) as error:
        validate_image(path, max_pixels=100)

    assert error.value.code == "INVALID_IMAGE"


def test_rejects_oversized_dimensions_before_decoding_pixels(monkeypatch, tmp_path):
    path = tmp_path / "oversized.png"

    class OversizedImage:
        format = "PNG"
        size = (11, 10)

        def __enter__(self):
            return self

        def __exit__(self, *_):
            return None

        def verify(self):
            raise AssertionError("oversized pixels must not be decoded")

    monkeypatch.setattr(Image, "open", lambda *_args, **_kwargs: OversizedImage())

    with pytest.raises(ProtocolError) as error:
        validate_image(path, max_pixels=100)

    assert error.value.code == "IMAGE_TOO_LARGE"


def _bilevel_png(path, size):
    Image.new("1", size).save(path, format="PNG")


def test_image_just_under_cap_is_not_rejected_by_pillow_default_threshold(tmp_path):
    default_threshold = Image.MAX_IMAGE_PIXELS
    cap = 100_000_000
    size = (10_000, 9_000)
    assert default_threshold < size[0] * size[1] < cap
    path = tmp_path / "large.png"
    _bilevel_png(path, size)

    info = validate_image(path, max_pixels=cap)

    assert (info.width, info.height) == size
    assert Image.MAX_IMAGE_PIXELS == default_threshold


@pytest.mark.parametrize(
    "size", [(10_000, 10_000), (10_000, 10_001), (20_000, 10_001)]
)
def test_image_at_or_above_cap_is_rejected_and_threshold_restored(tmp_path, size):
    default_threshold = Image.MAX_IMAGE_PIXELS
    path = tmp_path / "huge.png"
    _bilevel_png(path, size)

    with pytest.raises(ProtocolError) as error:
        validate_image(path, max_pixels=100_000_000)

    assert error.value.code == "IMAGE_TOO_LARGE"
    assert Image.MAX_IMAGE_PIXELS == default_threshold


def test_pillow_threshold_is_restored_after_invalid_image(tmp_path):
    default_threshold = Image.MAX_IMAGE_PIXELS
    path = tmp_path / "broken.png"
    path.write_bytes(b"not an image")

    with pytest.raises(ProtocolError):
        validate_image(path, max_pixels=100_000_000)

    assert Image.MAX_IMAGE_PIXELS == default_threshold
