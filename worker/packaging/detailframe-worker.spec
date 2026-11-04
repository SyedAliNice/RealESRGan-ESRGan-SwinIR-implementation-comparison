# PyInstaller spec: onedir CPU worker. Build with worker/packaging/build.ps1.
from pathlib import Path

from PyInstaller.utils.hooks import collect_data_files, collect_dynamic_libs, collect_submodules

worker_root = Path(SPECPATH).parent
src = worker_root / "src"

datas = collect_data_files("detailframe_worker", includes=["data/*.json"])
datas += collect_data_files("jsonschema")
datas += collect_data_files("jsonschema_specifications")
binaries = collect_dynamic_libs("torch")
hiddenimports = (
    collect_submodules("detailframe_worker")
    + collect_submodules("jsonschema")
    + collect_submodules("jsonschema_specifications")
    + collect_submodules("referencing")
)

a = Analysis(
    [str(src / "detailframe_worker" / "__main__.py")],
    pathex=[str(src)],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    excludes=["pytest", "tkinter", "torch.utils.tensorboard"],
    noarchive=False,
)
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="detailframe-worker",
    console=True,
    upx=False,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    upx=False,
    name="win-x64",
)
