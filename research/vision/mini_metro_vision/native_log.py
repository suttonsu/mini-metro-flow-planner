"""Bridge vision observations with the native planner's authoritative telemetry."""

from __future__ import annotations

import re
from pathlib import Path


FIELD = re.compile(r"\b([A-Za-z][A-Za-z0-9]*)=([^\s|]+)")


def latest_snapshot(path: str | Path) -> dict[str, object] | None:
    log_path = Path(path)
    if not log_path.is_file():
        return None
    latest = None
    for line in log_path.read_text(encoding="utf-8", errors="replace").splitlines():
        if "[Auto Planner]" in line and "city=" in line:
            latest = line
    if latest is None:
        return None
    values: dict[str, object] = {}
    for key, value in FIELD.findall(latest):
        if value.lstrip("-").isdigit():
            values[key] = int(value)
        elif value in {"True", "False"}:
            values[key] = value == "True"
        else:
            values[key] = value
    values["source"] = str(log_path)
    return values
