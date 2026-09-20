"""Execute the lightweight audit notebook and extract its rendered figures."""

from __future__ import annotations

import base64
import os
from pathlib import Path

import nbformat
from nbclient import NotebookClient


ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "01_dataset_audit.ipynb"
OUTPUT = ROOT / "01_dataset_audit.executed.ipynb"
FIGURES = ROOT.parent / "artifacts" / "notebook-audit"


def main() -> None:
    if not os.environ.get("MINI_METRO_DATASET_ZIP"):
        raise RuntimeError("MINI_METRO_DATASET_ZIP is required")
    notebook = nbformat.read(SOURCE, as_version=4)
    client = NotebookClient(notebook, timeout=300, kernel_name="python3")
    client.execute(cwd=str(ROOT))
    nbformat.write(notebook, OUTPUT)
    FIGURES.mkdir(parents=True, exist_ok=True)
    count = 0
    for cell in notebook.cells:
        for output in cell.get("outputs", []):
            image = output.get("data", {}).get("image/png")
            if image:
                count += 1
                (FIGURES / f"figure-{count:02d}.png").write_bytes(base64.b64decode(image))
    if count < 2:
        raise AssertionError(f"expected at least two rendered figures, found {count}")
    print(f"executed {OUTPUT}")
    print(f"extracted {count} figures to {FIGURES}")


if __name__ == "__main__":
    main()

