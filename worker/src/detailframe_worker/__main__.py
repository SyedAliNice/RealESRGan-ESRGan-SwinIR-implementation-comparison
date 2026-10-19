import argparse
from collections.abc import Sequence
from pathlib import Path

from detailframe_worker import __version__


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(prog="detailframe-worker")
    parser.add_argument(
        "--version",
        action="version",
        version=f"%(prog)s {__version__}",
    )
    parser.add_argument(
        "--protocol-stdio",
        action="store_true",
        help="communicate using the JSON Lines standard I/O protocol",
    )
    parser.add_argument(
        "--models-dir",
        type=Path,
        help="directory containing the model catalog and weights",
    )
    args = parser.parse_args(argv)

    if not args.protocol_stdio:
        parser.error("--protocol-stdio is required")
    if args.models_dir is None:
        parser.error("--models-dir is required")

    return args


def main() -> int:
    parse_args()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
