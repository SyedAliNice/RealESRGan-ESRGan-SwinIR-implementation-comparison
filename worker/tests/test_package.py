from detailframe_worker.__main__ import parse_args


def test_protocol_mode_requires_model_directory(tmp_path):
    args = parse_args(["--protocol-stdio", "--models-dir", str(tmp_path)])
    assert args.protocol_stdio is True
    assert args.models_dir == tmp_path
