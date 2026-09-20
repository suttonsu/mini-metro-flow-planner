"""Validate notebook schema and ordinary Python cell syntax."""

from __future__ import annotations

import ast
from pathlib import Path

import nbformat


ROOT = Path(__file__).resolve().parent


def ordinary_python(source: str) -> str:
    return "\n".join(
        line for line in source.splitlines()
        if not line.lstrip().startswith(("%", "!"))
    )


def main() -> None:
    notebooks = sorted(
        path for path in ROOT.glob("*.ipynb")
        if not path.name.endswith(".executed.ipynb")
    )
    if len(notebooks) != 3:
        raise AssertionError(f"expected three notebooks, found {len(notebooks)}")
    for path in notebooks:
        notebook = nbformat.read(path, as_version=4)
        nbformat.validate(notebook)
        cells = [cell for cell in notebook.cells if cell.cell_type == "code"]
        if not cells:
            raise AssertionError(f"no code cells: {path}")
        for index, cell in enumerate(cells):
            source = ordinary_python(cell.source)
            if source.strip():
                ast.parse(source, filename=f"{path.name}:cell-{index}")
        print(f"valid {path.name}: {len(cells)} code cells")


if __name__ == "__main__":
    main()
