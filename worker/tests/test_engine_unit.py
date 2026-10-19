import numpy as np
import pytest
import torch
from PIL import Image

from detailframe_worker import engine
from detailframe_worker.catalog import ModelVariant
from detailframe_worker.engine import EnhanceCancelled, ModelAdapter, enhance
from detailframe_worker.protocol import ProtocolError

SCALE = 2


class NearestUpscaler(torch.nn.Module):
    def __init__(self, scale):
        super().__init__()
        self.scale = scale
        self.calls = []

    def forward(self, tensor):
        self.calls.append(tuple(tensor.shape))
        assert not torch.is_grad_enabled()
        return torch.nn.functional.interpolate(tensor, scale_factor=self.scale, mode="nearest")


def make_variant(**overrides):
    values = dict(
        id="test-model",
        display_name="Test",
        architecture="test-nearest",
        scale=SCALE,
        weight_file="test.pth",
        weight_url="https://example.invalid/test.pth",
        sha256="0" * 64,
        license="test",
        provenance_url="https://example.invalid/test",
        size_bytes=None,
        devices=("cpu",),
        enabled=True,
        validation=(),
        expected_state_dict=(("test.weight", (1,)),),
        architecture_config_json='{"scale":2}',
        architecture_config_sha256="",
        validation_manifest_sha256="",
        validation_manifest_json="",
    )
    values.update(overrides)
    return ModelVariant(**values)


@pytest.fixture
def model(monkeypatch):
    built = []

    def build(variant, state_dict):
        module = NearestUpscaler(variant.scale)
        built.append(module)
        return module

    monkeypatch.setitem(engine.ADAPTERS, "test-nearest", ModelAdapter(build))
    monkeypatch.setattr(engine, "_load_state_dict", lambda variant, models_dir: {})
    return built


@pytest.fixture
def source(tmp_path):
    path = tmp_path / "source.png"
    pixels = np.arange(5 * 7 * 3, dtype=np.uint8).reshape(5, 7, 3)
    Image.fromarray(pixels, "RGB").save(path)
    return path, pixels


def run(tmp_path, source_path, *, output_format="png", progress=None, cancelled=None, **overrides):
    catalog = overrides.pop("catalog", (make_variant(),))
    original_loader = engine.load_catalog
    engine.load_catalog = lambda _path: catalog
    arguments = dict(
        model_id="test-model",
        scale=SCALE,
        device="cpu",
        input_path=source_path,
        output_path=tmp_path / "out.png",
        output_format=output_format,
        progress=progress or (lambda stage, fraction: None),
        cancelled=cancelled or (lambda: False),
        models_dir=tmp_path,
    )
    arguments.update(overrides)
    try:
        return enhance(**arguments)
    finally:
        engine.load_catalog = original_loader


def test_output_has_scaled_dimensions_and_expected_pixels(tmp_path, source, model):
    path, pixels = source

    info = run(tmp_path, path)

    assert (info.width, info.height, info.format) == (14, 10, "PNG")
    with Image.open(tmp_path / "out.png") as result:
        assert np.array_equal(np.asarray(result), pixels.repeat(2, 0).repeat(2, 1))
    assert path.exists()


def test_tiled_inference_matches_whole_image_and_reports_monotonic_progress(
    tmp_path, source, model, monkeypatch
):
    path, pixels = source
    monkeypatch.setattr(engine, "TILE_SIZE", 3)
    monkeypatch.setattr(engine, "TILE_PADDING", 1)
    reported = []

    run(tmp_path, path, progress=lambda stage, fraction: reported.append((stage, fraction)))

    with Image.open(tmp_path / "out.png") as result:
        assert np.array_equal(np.asarray(result), pixels.repeat(2, 0).repeat(2, 1))
    assert len(model[0].calls) == 6
    fractions = [fraction for _, fraction in reported]
    assert fractions == sorted(fractions)
    assert all(0.0 <= fraction <= 1.0 for fraction in fractions)
    assert {stage for stage, _ in reported} >= {"loading_model", "inference", "encoding"}


def test_model_output_is_clamped_and_converted_to_uint8(tmp_path, source, model, monkeypatch):
    path, _ = source
    monkeypatch.setitem(
        engine.ADAPTERS,
        "test-nearest",
        ModelAdapter(lambda variant, state: lambda tensor: torch.nn.functional.interpolate(
            tensor * 10 - 1, scale_factor=2, mode="nearest"
        )),
    )

    run(tmp_path, path)

    with Image.open(tmp_path / "out.png") as result:
        values = np.asarray(result)
    assert values.dtype == np.uint8
    assert values.min() == 0 and values.max() == 255


@pytest.mark.parametrize(
    ("output_format", "expected"),
    [("png", "PNG"), ("jpeg", "JPEG"), ("jpg", "JPEG"), ("webp", "WEBP")],
)
def test_encodes_requested_output_format(tmp_path, source, model, output_format, expected):
    path, _ = source
    output = tmp_path / "result.bin"

    info = run(tmp_path, path, output_format=output_format, output_path=output)

    assert info.format == expected
    with Image.open(output) as result:
        assert result.format == expected
        assert result.size == (14, 10)
    assert [entry.name for entry in tmp_path.iterdir() if entry.name.endswith(".tmp")] == []


def test_alpha_is_preserved_for_png_and_flattened_for_jpeg(tmp_path, model):
    path = tmp_path / "alpha.png"
    Image.new("RGBA", (4, 4), (255, 0, 0, 128)).save(path)

    run(tmp_path, path)
    jpeg = tmp_path / "out.jpg"
    run(tmp_path, path, output_format="jpeg", output_path=jpeg)

    with Image.open(tmp_path / "out.png") as result:
        assert result.mode == "RGBA" and result.size == (8, 8)
        assert result.getpixel((0, 0))[3] == 128
    with Image.open(jpeg) as result:
        assert result.mode == "RGB"


@pytest.mark.parametrize(
    ("overrides", "code"),
    [
        ({"model_id": "missing"}, "MODEL_UNAVAILABLE"),
        ({"scale": 4}, "UNSUPPORTED_SCALE"),
        ({"device": "cuda"}, "UNSUPPORTED_DEVICE"),
        ({"output_format": "gif"}, "INVALID_REQUEST"),
        ({"catalog": (make_variant(enabled=False),)}, "MODEL_UNAVAILABLE"),
        ({"catalog": (make_variant(architecture="unknown"),)}, "MODEL_UNAVAILABLE"),
        ({"catalog": ()}, "MODEL_UNAVAILABLE"),
    ],
)
def test_rejects_unsupported_requests_before_model_load(
    tmp_path, source, model, monkeypatch, overrides, code
):
    path, _ = source
    monkeypatch.setattr(
        engine, "_load_state_dict", lambda *args: pytest.fail("model must not load")
    )

    with pytest.raises(ProtocolError) as error:
        run(tmp_path, path, **overrides)

    assert error.value.code == code
    assert not (tmp_path / "out.png").exists()


def test_production_adapter_registry_is_empty():
    assert engine.ADAPTERS == {}


def test_rejects_invalid_and_oversized_input_without_output(tmp_path, model):
    bad = tmp_path / "bad.png"
    bad.write_bytes(b"nope")
    with pytest.raises(ProtocolError) as error:
        run(tmp_path, bad)
    assert error.value.code == "INVALID_IMAGE"

    big = tmp_path / "big.png"
    Image.new("RGB", (4, 4)).save(big)
    with pytest.raises(ProtocolError) as error:
        run(tmp_path, big, max_pixels=15)
    assert error.value.code == "IMAGE_TOO_LARGE"
    assert not (tmp_path / "out.png").exists()


def test_rejects_output_path_equal_to_input(tmp_path, source, model):
    path, _ = source

    with pytest.raises(ProtocolError) as error:
        run(tmp_path, path, output_path=path)

    assert error.value.code == "INVALID_REQUEST"
    assert Image.open(path).size == (7, 5)


def test_rejects_model_output_with_wrong_shape(tmp_path, source, model, monkeypatch):
    path, _ = source
    monkeypatch.setitem(
        engine.ADAPTERS, "test-nearest", ModelAdapter(lambda variant, state: lambda tensor: tensor)
    )

    with pytest.raises(ProtocolError) as error:
        run(tmp_path, path)

    assert error.value.code == "INFERENCE_FAILED"
    assert not (tmp_path / "out.png").exists()


def test_rejects_non_finite_model_output(tmp_path, source, model, monkeypatch):
    path, _ = source
    monkeypatch.setitem(
        engine.ADAPTERS,
        "test-nearest",
        ModelAdapter(lambda variant, state: lambda tensor: torch.full(
            (1, 3, tensor.shape[2] * 2, tensor.shape[3] * 2), float("nan")
        )),
    )

    with pytest.raises(ProtocolError) as error:
        run(tmp_path, path)

    assert error.value.code == "INFERENCE_FAILED"


@pytest.mark.parametrize("phase", ["start", "loaded", "tile", "encoding"])
def test_cancellation_at_each_phase_leaves_no_output_and_keeps_existing_result(
    tmp_path, source, model, monkeypatch, phase
):
    path, _ = source
    monkeypatch.setattr(engine, "TILE_SIZE", 3)
    existing = tmp_path / "out.png"
    existing.write_bytes(b"earlier result")
    state = {"stage": None, "tiles": 0}

    def progress(stage, fraction):
        state["stage"] = stage
        if stage == "inference":
            state["tiles"] += 1

    def cancelled():
        if phase == "start":
            return True
        if phase == "loaded":
            return state["stage"] == "loading_model"
        if phase == "tile":
            return state["tiles"] == 2
        return state["stage"] == "encoding"

    with pytest.raises(EnhanceCancelled):
        run(tmp_path, path, progress=progress, cancelled=cancelled)

    assert existing.read_bytes() == b"earlier result"
    assert sorted(entry.name for entry in tmp_path.iterdir()) == ["out.png", "source.png"]


def test_failed_encode_leaves_existing_result_and_no_temporary_file(
    tmp_path, source, model, monkeypatch
):
    path, _ = source
    existing = tmp_path / "out.png"
    existing.write_bytes(b"earlier result")
    monkeypatch.setattr(engine, "_verify_encoded", lambda *args: (_ for _ in ()).throw(
        ProtocolError("OUTPUT_FAILED", "bad")
    ))

    with pytest.raises(ProtocolError) as error:
        run(tmp_path, path)

    assert error.value.code == "OUTPUT_FAILED"
    assert existing.read_bytes() == b"earlier result"
    assert sorted(entry.name for entry in tmp_path.iterdir()) == ["out.png", "source.png"]


def test_replace_failure_does_not_report_complete_progress(
    tmp_path, source, model, monkeypatch
):
    path, _ = source
    existing = tmp_path / "out.png"
    existing.write_bytes(b"earlier result")
    reported = []

    def fail_replace(_temporary, _output):
        raise OSError("publication failed")

    monkeypatch.setattr(engine.os, "replace", fail_replace)

    with pytest.raises(ProtocolError) as error:
        run(tmp_path, path, progress=lambda stage, fraction: reported.append((stage, fraction)))

    assert error.value.code == "INFERENCE_FAILED"
    assert ("encoding", 1.0) not in reported
    assert existing.read_bytes() == b"earlier result"
    assert sorted(entry.name for entry in tmp_path.iterdir()) == ["out.png", "source.png"]
