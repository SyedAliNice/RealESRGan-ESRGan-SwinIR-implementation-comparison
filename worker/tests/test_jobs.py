from uuid import UUID

from detailframe_worker.jobs import JobWorkspace


JOB_ID = UUID("00000000-0000-4000-8000-000000000002")


def test_job_paths_are_unique_and_contained_under_private_root(tmp_path):
    root = tmp_path / "private"
    first = JobWorkspace.create(root, JOB_ID)
    second = JobWorkspace.create(root, JOB_ID)

    assert first.directory != second.directory
    assert first.directory.parent == root.resolve()
    assert first.input_path.parent == first.directory
    assert first.output_path.parent == first.directory
    assert first.input_path != first.output_path

    first.cleanup()
    second.cleanup()


def test_cleanup_removes_only_the_job_directory(tmp_path):
    root = tmp_path / "private"
    root.mkdir()
    unrelated = root / "keep.txt"
    unrelated.write_text("preserve", encoding="utf-8")
    workspace = JobWorkspace.create(root, JOB_ID)
    workspace.input_path.write_bytes(b"input")
    workspace.output_path.write_bytes(b"partial output")

    workspace.cleanup()

    assert not workspace.directory.exists()
    assert unrelated.read_text(encoding="utf-8") == "preserve"


def test_cleanup_is_safe_when_job_files_or_directory_are_already_absent(tmp_path):
    workspace = JobWorkspace.create(tmp_path / "private", JOB_ID)
    workspace.input_path.unlink(missing_ok=True)
    workspace.output_path.unlink(missing_ok=True)
    workspace.directory.rmdir()

    workspace.cleanup()
    workspace.cleanup()

    assert not workspace.directory.exists()
