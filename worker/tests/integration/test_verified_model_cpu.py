from pathlib import Path

import pytest
from PIL import Image

from detailframe_worker import engine
from detailframe_worker.catalog import load_catalog
from detailframe_worker.protocol import ProtocolError

ROOT = Path(__file__).resolve().parents[3]
MODELS_DIR = ROOT / "shared"


def test_empty_production_catalog_has_no_unverified_models():
    assert load_catalog(MODELS_DIR / "models.json") == ()
    assert engine.ADAPTERS == {}


def test_production_catalog_returns_model_unavailable_without_output(tmp_path):
    source = tmp_path / "source.png"
    output = tmp_path / "output.png"
    Image.new("RGB", (2, 2), "blue").save(source)

    with pytest.raises(ProtocolError) as error:
        engine.enhance(
            model_id="unverified-model",
            scale=2,
            device="cpu",
            input_path=source,
            output_path=output,
            output_format="png",
            progress=lambda _stage, _fraction: None,
            cancelled=lambda: False,
            models_dir=MODELS_DIR,
        )

    assert error.value.code == "MODEL_UNAVAILABLE"
    assert not output.exists()
