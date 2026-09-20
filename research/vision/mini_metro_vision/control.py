"""Atomic, versioned contract between the vision sidecar and the C# planner."""

from __future__ import annotations

import os
import time
from collections import Counter
from pathlib import Path
from typing import Any


SCHEMA_VERSION = 1


def build_control_values(record: dict[str, Any], enforce: bool) -> dict[str, str]:
    context = record.get("context") or {}
    stations = record.get("stations") or []
    native = record.get("native") or {}
    confidences = [float(item.get("confidence", 0.0)) for item in stations]
    labels = Counter(str(item.get("label", "station")) for item in stations)
    native_count = native.get("stations", -1)
    delta = record.get("station_count_delta", "")
    return {
        "schema": str(SCHEMA_VERSION),
        "control_enabled": "true" if enforce else "false",
        "sequence": str(int(record.get("sequence", 0))),
        "frame_monotonic": f"{float(record.get('timestamp', 0.0)):.6f}",
        "published_unix": f"{time.time():.6f}",
        "width": str(int(record.get("width", 0))),
        "height": str(int(record.get("height", 0))),
        "context_label": str(context.get("label", "unknown")),
        "context_confidence": f"{float(context.get('confidence', 0.0)):.6f}",
        "station_count": str(len(stations)),
        "mean_station_confidence": (
            f"{sum(confidences) / len(confidences):.6f}" if confidences else "0.000000"
        ),
        "native_station_count": str(int(native_count)) if native_count is not None else "-1",
        "station_count_delta": str(delta),
        "shape_counts": ",".join(f"{key}:{labels[key]}" for key in sorted(labels)),
    }


def publish_control_state(
    path: str | Path,
    record: dict[str, Any],
    *,
    enforce: bool,
) -> None:
    """Publish one complete state using replace-on-close semantics.

    The contract deliberately uses simple key/value text so the Unity plugin can
    parse it without adding a JSON assembly to the game's legacy Mono runtime.
    """

    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    values = build_control_values(record, enforce)
    temporary = target.with_name(target.name + f".{os.getpid()}.tmp")
    temporary.write_text(
        "".join(f"{key}={value}\n" for key, value in values.items()),
        encoding="utf-8",
    )
    os.replace(temporary, target)
