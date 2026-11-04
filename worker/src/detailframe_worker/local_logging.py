import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path


def configure_local_logging(
    directory: Path, *, max_bytes: int = 1024 * 1024, backup_count: int = 3
) -> logging.Logger:
    directory.mkdir(parents=True, exist_ok=True)
    logger = logging.getLogger("detailframe_worker")
    logger.setLevel(logging.INFO)
    logger.propagate = False
    handler = RotatingFileHandler(
        directory / "worker.log", maxBytes=max_bytes, backupCount=backup_count,
        encoding="utf-8",
    )
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logger.addHandler(handler)
    return logger
