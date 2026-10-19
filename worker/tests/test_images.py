import pytest
from PIL import Image

from detailframe_worker.images import validate_image
from detailframe_worker.protocol import ProtocolError


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
