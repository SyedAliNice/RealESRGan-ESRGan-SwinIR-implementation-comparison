import logging

from detailframe_worker.local_logging import configure_local_logging


def test_local_log_rotates_with_bounded_retention(tmp_path):
    logger = configure_local_logging(tmp_path, max_bytes=120, backup_count=2)
    try:
        for _ in range(20):
            logger.warning("worker operation failed")
        files = list(tmp_path.glob("worker.log*"))
        assert 1 < len(files) <= 3
        assert all(path.stat().st_size <= 120 for path in files)
    finally:
        for handler in list(logger.handlers):
            handler.close()
            logger.removeHandler(handler)
