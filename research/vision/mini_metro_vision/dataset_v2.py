"""Build a versioned Colab-ready dataset from legacy labels and current captures."""

from __future__ import annotations

import hashlib
import json
import shutil
import zipfile
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from PIL import Image

from .dataset import LegacyArchive


DATASET_SCHEMA = "mini-metro-vision-v2"
CATEGORY_NAMES = (
    "circle", "triangle", "square", "drop", "pentagon",
    "rhombus", "cross", "star", "station",
)


def _short_hash(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]


def _archive_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def _context_split(samples: list[Any]) -> dict[str, str]:
    by_label: dict[str, list[Any]] = defaultdict(list)
    for sample in samples:
        by_label[sample.label].append(sample)
    result: dict[str, str] = {}
    for label_samples in by_label.values():
        training = sorted(
            (item for item in label_samples if item.split == "train"),
            key=lambda item: item.member,
        )
        for index, sample in enumerate(training):
            result[sample.member] = "val" if index % 6 == 0 else "train"
        for sample in label_samples:
            if sample.split == "test":
                result[sample.member] = "test"
    return result


def _detection_split(samples: list[Any]) -> dict[str, str]:
    training = sorted(
        (item for item in samples if item.split == "train"),
        key=lambda item: item.image_member,
    )
    result = {
        sample.image_member: ("val" if index % 6 == 0 else "train")
        for index, sample in enumerate(training)
    }
    result.update(
        {sample.image_member: "test" for sample in samples if sample.split == "test"}
    )
    return result


def _load_current_records(path: Path | None) -> list[dict[str, Any]]:
    if path is None or not path.is_file():
        return []
    return [
        json.loads(line)
        for line in path.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]


def build_dataset(
    archive_path: str | Path,
    output: str | Path,
    *,
    current_observations: str | Path | None = None,
    current_frames: str | Path | None = None,
) -> dict[str, Any]:
    archive_path = Path(archive_path).resolve()
    output = Path(output).resolve()
    if output.exists():
        raise FileExistsError(f"dataset output already exists: {output}")
    archive = LegacyArchive(archive_path)
    context_samples = archive.context_samples()
    station_samples = archive.station_samples()
    context_splits = _context_split(context_samples)
    detection_splits = _detection_split(station_samples)

    context_counts: Counter[tuple[str, str]] = Counter()
    detection_documents: dict[str, dict[str, Any]] = {
        split: {
            "info": {
                "description": "Mini Metro vision v2: legacy ground truth plus current-version reviewed seed captures",
                "version": "2.0-seed",
                "year": datetime.now(timezone.utc).year,
            },
            "licenses": [
                {"id": 1, "name": "MIT", "url": "https://opensource.org/license/mit"}
            ],
            "categories": [
                {"id": index + 1, "name": name, "supercategory": "station"}
                for index, name in enumerate(CATEGORY_NAMES)
            ],
            "images": [],
            "annotations": [],
        }
        for split in ("train", "val", "test")
    }
    category_ids = {name: index + 1 for index, name in enumerate(CATEGORY_NAMES)}
    next_image_id = 1
    next_annotation_id = 1

    for source_split in ("train", "test"):
        for sample, pixels in archive.iter_context_images(source_split):
            split = context_splits[sample.member]
            name = f"legacy-{_short_hash(sample.member)}.png"
            destination = output / "classification" / split / sample.label / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            Image.fromarray(pixels).save(destination)
            context_counts[(split, sample.label)] += 1

    for sample in station_samples:
        split = detection_splits[sample.image_member]
        name = f"legacy-{_short_hash(sample.image_member)}.png"
        destination = output / "detection" / "images" / split / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        Image.fromarray(archive.read_image(sample.image_member)).save(destination)
        document = detection_documents[split]
        image_id = next_image_id
        next_image_id += 1
        document["images"].append(
            {
                "id": image_id,
                "file_name": f"images/{split}/{name}",
                "width": sample.width,
                "height": sample.height,
                "source": "legacy_ground_truth",
                "source_member": sample.image_member,
            }
        )
        for box in sample.boxes:
            width = max(0.0, box.xmax - box.xmin)
            height = max(0.0, box.ymax - box.ymin)
            label = box.label if box.label in category_ids else "station"
            document["annotations"].append(
                {
                    "id": next_annotation_id,
                    "image_id": image_id,
                    "category_id": category_ids[label],
                    "bbox": [box.xmin, box.ymin, width, height],
                    "area": width * height,
                    "iscrowd": 0,
                    "review_status": "ground_truth",
                }
            )
            next_annotation_id += 1

    observation_path = Path(current_observations).resolve() if current_observations else None
    frame_root = Path(current_frames).resolve() if current_frames else None
    current_added = 0
    for record in _load_current_records(observation_path):
        sequence = int(record.get("sequence", 0))
        source = frame_root / f"frame-{sequence:05d}.png" if frame_root else None
        if source is None or not source.is_file():
            continue
        name = f"current-{_short_hash(str(source.resolve()))}.png"
        class_destination = output / "classification" / "train" / "game_play" / name
        class_destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, class_destination)
        context_counts[("train", "game_play")] += 1

        detection_destination = output / "detection" / "images" / "train" / name
        detection_destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, detection_destination)
        image_id = next_image_id
        next_image_id += 1
        width = int(record.get("width", 0))
        height = int(record.get("height", 0))
        detection_documents["train"]["images"].append(
            {
                "id": image_id,
                "file_name": f"images/train/{name}",
                "width": width,
                "height": height,
                "source": "current_pseudolabel",
                "source_member": str(source),
                "native_station_count": (record.get("native") or {}).get("stations"),
            }
        )
        for station in record.get("stations") or []:
            xmin = float(station["xmin"])
            ymin = float(station["ymin"])
            box_width = max(0.0, float(station["xmax"]) - xmin)
            box_height = max(0.0, float(station["ymax"]) - ymin)
            label = str(station.get("label", "station"))
            if label not in category_ids:
                label = "station"
            detection_documents["train"]["annotations"].append(
                {
                    "id": next_annotation_id,
                    "image_id": image_id,
                    "category_id": category_ids[label],
                    "bbox": [xmin, ymin, box_width, box_height],
                    "area": box_width * box_height,
                    "iscrowd": 0,
                    "score": float(station.get("confidence", 0.0)),
                    "review_status": "pseudo_needs_human_review",
                }
            )
            next_annotation_id += 1
        current_added += 1

    annotations_root = output / "detection" / "annotations"
    annotations_root.mkdir(parents=True, exist_ok=True)
    detection_counts: dict[str, dict[str, int]] = {}
    for split, document in detection_documents.items():
        (annotations_root / f"instances_{split}.json").write_text(
            json.dumps(document, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        detection_counts[split] = {
            "images": len(document["images"]),
            "annotations": len(document["annotations"]),
        }

    labels = sorted({key[1] for key in context_counts})
    manifest = {
        "schema": DATASET_SCHEMA,
        "version": "2.0-seed",
        "created_utc": datetime.now(timezone.utc).isoformat(),
        "legacy_archive": str(archive_path),
        "legacy_archive_sha256": _archive_sha256(archive_path),
        "current_observations": str(observation_path) if observation_path else None,
        "current_frames": str(frame_root) if frame_root else None,
        "current_pseudolabel_images": current_added,
        "classification_counts": {
            split: {label: context_counts[(split, label)] for label in labels}
            for split in ("train", "val", "test")
        },
        "detection_counts": detection_counts,
        "categories": list(CATEGORY_NAMES),
        "split_policy": {
            "legacy_test": "preserved as test",
            "legacy_train": "deterministic approximately 5/6 train and 1/6 validation",
            "current_capture": "train only; pseudo labels require human review before promotion",
        },
    }
    output.mkdir(parents=True, exist_ok=True)
    (output / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    (output / "DATASET_CARD.md").write_text(
        "# Mini Metro Vision v2 seed dataset\n\n"
        "This dataset combines MIT-licensed legacy Mini Metro labels with current-version "
        "captures. Legacy test samples remain isolated. Current captures are placed only in "
        "training and carry `pseudo_needs_human_review`; they must not be treated as ground "
        "truth until reviewed. The detector can use one `station` class or the shape categories "
        "listed in `manifest.json`.\n",
        encoding="utf-8",
    )
    return manifest


def validate_dataset(root: str | Path) -> dict[str, Any]:
    root = Path(root).resolve()
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
    errors: list[str] = []
    summaries: dict[str, Any] = {}
    seen_sources: dict[str, str] = {}
    for split in ("train", "val", "test"):
        path = root / "detection" / "annotations" / f"instances_{split}.json"
        document = json.loads(path.read_text(encoding="utf-8"))
        image_by_id = {int(item["id"]): item for item in document["images"]}
        for image in document["images"]:
            image_path = root / "detection" / image["file_name"]
            if not image_path.is_file():
                errors.append(f"missing image: {image_path}")
            source = str(image.get("source_member", image["file_name"]))
            previous = seen_sources.get(source)
            if previous is not None and previous != split:
                errors.append(f"source split leakage: {source} in {previous} and {split}")
            seen_sources[source] = split
        for annotation in document["annotations"]:
            image = image_by_id.get(int(annotation["image_id"]))
            if image is None:
                errors.append(f"orphan annotation {annotation['id']}")
                continue
            x, y, width, height = (float(value) for value in annotation["bbox"])
            if width <= 0 or height <= 0:
                errors.append(f"non-positive box {annotation['id']}")
            if (x < 0 or y < 0 or x + width > float(image["width"]) + 1
                    or y + height > float(image["height"]) + 1):
                errors.append(f"out-of-bounds box {annotation['id']}")
        summaries[split] = {
            "images": len(document["images"]),
            "annotations": len(document["annotations"]),
        }
    classification_counts = Counter()
    for split in ("train", "val", "test"):
        split_root = root / "classification" / split
        if split_root.is_dir():
            for image in split_root.rglob("*.png"):
                classification_counts[(split, image.parent.name)] += 1
    result = {
        "valid": not errors,
        "schema": manifest.get("schema"),
        "detection": summaries,
        "classification": {
            split: {
                label: count
                for (item_split, label), count in sorted(classification_counts.items())
                if item_split == split
            }
            for split in ("train", "val", "test")
        },
        "errors": errors,
    }
    if errors:
        raise ValueError(json.dumps(result, ensure_ascii=False, indent=2))
    return result


def package_dataset(root: str | Path, destination: str | Path) -> Path:
    root = Path(root).resolve()
    destination = Path(destination).resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(item for item in root.rglob("*") if item.is_file()):
            archive.write(path, Path(root.name) / path.relative_to(root))
    return destination

