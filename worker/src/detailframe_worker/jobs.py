import shutil
import tempfile
from dataclasses import dataclass
from pathlib import Path
from uuid import UUID


@dataclass(frozen=True)
class JobWorkspace:
    root: Path
    directory: Path
    input_path: Path
    output_path: Path

    @classmethod
    def create(cls, root: Path, job_id: UUID) -> "JobWorkspace":
        root.mkdir(parents=True, exist_ok=True)
        private_root = root.resolve(strict=True)
        if not private_root.is_dir():
            raise NotADirectoryError(private_root)

        directory = Path(
            tempfile.mkdtemp(prefix=f"{job_id}-", dir=private_root)
        ).resolve(strict=True)
        return cls(
            root=private_root,
            directory=directory,
            input_path=directory / "input",
            output_path=directory / "output",
        )

    def cleanup(self) -> None:
        try:
            resolved_root = self.root.resolve(strict=True)
            resolved_parent = self.directory.parent.resolve(strict=True)
        except FileNotFoundError:
            return
        if resolved_parent != resolved_root:
            raise ValueError("job directory is not directly beneath its private root")
        if self.directory.is_symlink():
            self.directory.unlink(missing_ok=True)
        elif self.directory.exists():
            shutil.rmtree(self.directory)
