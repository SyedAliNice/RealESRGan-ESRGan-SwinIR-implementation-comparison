import argparse
import logging
import os
import sys
from collections.abc import Sequence
from pathlib import Path

from detailframe_worker import __version__
from detailframe_worker.worker import run_worker
from detailframe_worker.local_logging import configure_local_logging


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
    args = parse_args()
    local_app_data = os.environ.get("LOCALAPPDATA")
    logger = logging.getLogger("detailframe_worker")
    if local_app_data:
        try:
            logger = configure_local_logging(Path(local_app_data) / "DetailFrame" / "Logs")
        except OSError:
            pass
    logger.info("Worker started")
    try:
        result = run_worker(sys.stdin, sys.stdout, sys.stderr, args.models_dir)
        logger.info("Worker exited with status %d", result)
        return result
    finally:
        for handler in list(logger.handlers):
            handler.close()
            logger.removeHandler(handler)


if __name__ == "__main__":
    raise SystemExit(main())
