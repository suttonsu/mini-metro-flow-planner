"""Integrity and promotion checks for framework-native Colab model bundles."""

from __future__ import annotations

import hashlib
import json
import zipfile
from pathlib import Path, PurePosixPath
from typing import BinaryIO, Callable


SCHEMA = "mini-metro-model-bundle-v1"
WEIGHT_SUFFIXES = (".keras", ".weights.h5", ".h5", ".pt", ".pth")


def _sha256(stream: BinaryIO) -> str:
    digest = hashlib.sha256()
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(chunk)
    return digest.hexdigest()


def _safe_relative(value: str) -> PurePosixPath:
    path = PurePosixPath(value.replace("\\", "/"))
    if path.is_absolute() or not path.parts or any(part in ("", ".", "..") for part in path.parts):
        raise ValueError(f"unsafe bundle path: {value!r}")
    return path


def _read_directory(source: Path):
    manifests = list(source.rglob("release-manifest.json"))
    if len(manifests) != 1:
        raise ValueError(f"expected one release-manifest.json, found {len(manifests)}")
    root = manifests[0].parent.resolve()

    def open_file(relative: PurePosixPath):
        path = (root / Path(*relative.parts)).resolve()
        if root not in path.parents and path != root:
            raise ValueError(f"bundle path escapes root: {relative}")
        return path.open("rb")

    manifest = json.loads(manifests[0].read_text(encoding="utf-8"))
    return manifest, open_file


def _read_zip(source: Path):
    archive = zipfile.ZipFile(source)
    names = [name for name in archive.namelist() if not name.endswith("/")]
    manifests = [name for name in names if PurePosixPath(name).name == "release-manifest.json"]
    if len(manifests) != 1:
        archive.close()
        raise ValueError(f"expected one release-manifest.json, found {len(manifests)}")
    manifest_name = _safe_relative(manifests[0])
    prefix = manifest_name.parent
    manifest = json.loads(archive.read(str(manifest_name)).decode("utf-8"))

    def open_file(relative: PurePosixPath):
        member = prefix / relative
        return archive.open(str(member), "r")

    return manifest, open_file, archive


def _result_candidates(
    results: object,
    metric: str,
    threshold: float,
    comparator: Callable[[float, float], bool],
) -> tuple[list[str], list[str]]:
    passing: list[str] = []
    errors: list[str] = []
    if not isinstance(results, list) or not results:
        return passing, [f"missing experiment results for {metric}"]
    for item in results:
        if not isinstance(item, dict) or not isinstance(item.get("id"), str):
            errors.append(f"malformed result entry for {metric}")
            continue
        try:
            value = float(item[metric])
        except (KeyError, TypeError, ValueError):
            errors.append(f"{item['id']}: missing numeric {metric}")
            continue
        if comparator(value, threshold):
            passing.append(item["id"])
    return passing, errors


def validate_model_bundle(
    source: Path,
    *,
    minimum_scene_macro_f1: float = 0.95,
    minimum_detection_f1: float = 0.95,
    maximum_count_mae: float = 0.5,
    minimum_current_sessions: int = 3,
) -> dict[str, object]:
    """Validate hashes and report whether a bundle has enough evidence for promotion."""

    source = Path(source)
    closeable = None
    if source.is_dir():
        manifest, open_file = _read_directory(source)
    elif source.is_file() and zipfile.is_zipfile(source):
        manifest, open_file, closeable = _read_zip(source)
    else:
        raise ValueError(f"bundle must be a directory or ZIP archive: {source}")

    errors: list[str] = []
    checked_files = 0
    weight_files: list[str] = []
    try:
        if manifest.get("schema") != SCHEMA:
            errors.append(f"unsupported schema: {manifest.get('schema')!r}")
        files = manifest.get("files")
        if not isinstance(files, dict) or not files:
            errors.append("manifest files map is missing or empty")
            files = {}
        for name, expected in files.items():
            try:
                relative = _safe_relative(str(name))
                if not isinstance(expected, str) or len(expected) != 64:
                    raise ValueError("invalid SHA-256 value")
                with open_file(relative) as stream:
                    actual = _sha256(stream)
                checked_files += 1
                if actual.lower() != expected.lower():
                    errors.append(f"hash mismatch: {relative}")
                lower_name = str(relative).lower()
                if lower_name.endswith(WEIGHT_SUFFIXES):
                    weight_files.append(str(relative))
            except (KeyError, OSError, ValueError, zipfile.BadZipFile) as exc:
                errors.append(f"{name}: {exc}")
    finally:
        if closeable is not None:
            closeable.close()

    if not weight_files:
        errors.append("bundle contains no framework-native weight file")

    scene_passing, scene_errors = _result_candidates(
        manifest.get("scene_results"),
        "macro_f1",
        minimum_scene_macro_f1,
        lambda value, threshold: value >= threshold,
    )
    detection_passing, detection_errors = _result_candidates(
        manifest.get("detection_results"),
        "f1_iou50",
        minimum_detection_f1,
        lambda value, threshold: value >= threshold,
    )
    count_passing, count_errors = _result_candidates(
        manifest.get("detection_results"),
        "count_mae",
        maximum_count_mae,
        lambda value, threshold: value <= threshold,
    )
    errors.extend(scene_errors + detection_errors + count_errors)
    detection_candidates = sorted(set(detection_passing).intersection(count_passing))

    evidence = manifest.get("promotion_evidence", {})
    sessions = evidence.get("current_session_evaluations", []) if isinstance(evidence, dict) else []
    if not isinstance(sessions, list):
        sessions = []
        errors.append("promotion_evidence.current_session_evaluations must be a list")
    distinct_sessions = {
        str(item.get("session_id"))
        for item in sessions
        if isinstance(item, dict) and item.get("session_id")
    }
    integrity_valid = not errors
    promotion_reasons: list[str] = []
    if not scene_passing:
        promotion_reasons.append("no scene model meets macro-F1 threshold")
    if not detection_candidates:
        promotion_reasons.append("no detector meets both F1 and count-MAE thresholds")
    if len(distinct_sessions) < minimum_current_sessions:
        promotion_reasons.append(
            f"current-version session evidence {len(distinct_sessions)}/{minimum_current_sessions}"
        )
    if not integrity_valid:
        promotion_reasons.append("bundle integrity validation failed")

    return {
        "source": str(source.resolve()),
        "schema": manifest.get("schema"),
        "integrity_valid": integrity_valid,
        "checked_files": checked_files,
        "weight_files": sorted(weight_files),
        "errors": errors,
        "thresholds": {
            "minimum_scene_macro_f1": minimum_scene_macro_f1,
            "minimum_detection_f1_iou50": minimum_detection_f1,
            "maximum_detection_count_mae": maximum_count_mae,
            "minimum_current_sessions": minimum_current_sessions,
        },
        "passing_scene_candidates": sorted(scene_passing),
        "passing_detection_candidates": detection_candidates,
        "current_session_count": len(distinct_sessions),
        "promotion_eligible": integrity_valid and not promotion_reasons,
        "promotion_reasons": promotion_reasons,
    }
