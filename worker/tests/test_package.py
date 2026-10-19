import tomllib
from pathlib import Path

from detailframe_worker.__main__ import parse_args


def test_python_requirement_matches_verified_cpu_wheel_range():
    pyproject = Path(__file__).parents[1] / "pyproject.toml"
    with pyproject.open("rb") as metadata_file:
        metadata = tomllib.load(metadata_file)

    assert metadata["project"]["requires-python"] == ">=3.11,<3.15"


def test_protocol_mode_requires_model_directory(tmp_path):
    args = parse_args(["--protocol-stdio", "--models-dir", str(tmp_path)])
    assert args.protocol_stdio is True
    assert args.models_dir == tmp_path
