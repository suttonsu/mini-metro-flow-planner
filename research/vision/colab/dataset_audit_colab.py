"""Self-contained Colab/local audit for the Mini Metro v2 dataset package."""

from __future__ import annotations

import json
import os
import shutil
import zipfile
from collections import Counter
from pathlib import Path

import matplotlib.patches as patches
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image


def prepare_dataset() -> tuple[Path, dict]:
    dataset_zip = Path(os.environ.get("MINI_METRO_DATASET_ZIP", "/content/mini_metro_v2_seed.zip"))
    work_root = Path(os.environ.get("MINI_METRO_WORK_ROOT", "/content/mini_metro_work"))
    if not dataset_zip.is_file():
        try:
            from google.colab import files

            print("Upload mini_metro_v2_seed.zip")
            uploaded = files.upload()
            dataset_zip = Path("/content") / next(iter(uploaded))
        except ImportError as exc:
            raise FileNotFoundError("Set MINI_METRO_DATASET_ZIP or upload the package in Colab") from exc
    if work_root.exists():
        shutil.rmtree(work_root)
    work_root.mkdir(parents=True)
    with zipfile.ZipFile(dataset_zip) as archive:
        archive.extractall(work_root)
    candidates = [path.parent for path in work_root.rglob("manifest.json")]
    if len(candidates) != 1:
        raise ValueError(f"expected one dataset root, found {candidates}")
    root = candidates[0]
    manifest = json.loads((root / "manifest.json").read_text(encoding="utf-8"))
    if manifest.get("schema") != "mini-metro-vision-v2":
        raise ValueError(f"unexpected schema: {manifest.get('schema')}")
    return root, manifest


def audit(root: Path, manifest: dict) -> tuple[list[dict], dict]:
    errors: list[str] = []
    rows: list[dict] = []
    documents: dict[str, dict] = {}
    seen_sources: dict[str, str] = {}
    for split in ("train", "val", "test"):
        document = json.loads(
            (root / "detection" / "annotations" / f"instances_{split}.json").read_text()
        )
        documents[split] = document
        images = {int(item["id"]): item for item in document["images"]}
        for item in images.values():
            path = root / "detection" / item["file_name"]
            if not path.is_file():
                errors.append(f"missing image: {path}")
            source = str(item.get("source_member", item["file_name"]))
            if source in seen_sources and seen_sources[source] != split:
                errors.append(f"source split leakage: {source}")
            seen_sources[source] = split
        for annotation in document["annotations"]:
            image = images.get(int(annotation["image_id"]))
            if image is None:
                errors.append(f"orphan annotation: {annotation['id']}")
                continue
            x, y, width, height = map(float, annotation["bbox"])
            if min(x, y) < 0 or width <= 0 or height <= 0:
                errors.append(f"invalid box: {annotation['id']}")
            if x + width > image["width"] + 1 or y + height > image["height"] + 1:
                errors.append(f"out-of-bounds box: {annotation['id']}")
            if split != "train" and annotation.get("review_status") != "ground_truth":
                errors.append(f"unreviewed label outside train: {annotation['id']}")
        rows.append({"split": split, "images": len(images), "boxes": len(document["annotations"])})
    if errors:
        raise AssertionError("\n".join(errors[:30]))
    return rows, documents


def plot_counts(manifest: dict, rows: list[dict]) -> None:
    plt.style.use("seaborn-v0_8-whitegrid")
    figure, axes = plt.subplots(1, 2, figsize=(13, 4.5))
    scene_counts = manifest["classification_counts"]
    labels = sorted(scene_counts["train"])
    x_values = np.arange(len(labels))
    for offset, split in zip((-0.25, 0, 0.25), ("train", "val", "test")):
        axes[0].bar(
            x_values + offset,
            [scene_counts[split][label] for label in labels],
            0.24,
            label=split,
        )
    axes[0].set_xticks(x_values, labels, rotation=35, ha="right")
    axes[0].set_title("Scene samples by split and class")
    axes[0].set_ylabel("Images")
    axes[0].legend()
    axes[1].bar([row["split"] for row in rows], [row["images"] for row in rows], label="images")
    axes[1].bar(
        [row["split"] for row in rows],
        [row["boxes"] for row in rows],
        bottom=[row["images"] for row in rows],
        label="boxes",
    )
    axes[1].set_title("Detection corpus size")
    axes[1].set_ylabel("Count")
    axes[1].legend()
    plt.tight_layout()
    plt.show()


def plot_examples(root: Path, documents: dict[str, dict]) -> None:
    figure, axes = plt.subplots(1, 3, figsize=(16, 5))
    for axis, split in zip(axes, ("train", "val", "test")):
        document = documents[split]
        image_info = document["images"][0]
        annotations = [item for item in document["annotations"] if item["image_id"] == image_info["id"]]
        image = Image.open(root / "detection" / image_info["file_name"]).convert("RGB")
        axis.imshow(image)
        for annotation in annotations:
            x, y, width, height = annotation["bbox"]
            axis.add_patch(
                patches.Rectangle((x, y), width, height, fill=False, edgecolor="#00d46a", linewidth=1.5)
            )
        axis.set_title(f"{split}: {len(annotations)} stations")
        axis.axis("off")
    plt.tight_layout()
    plt.show()


if __name__ == "__main__":
    dataset_root, dataset_manifest = prepare_dataset()
    audit_rows, audit_documents = audit(dataset_root, dataset_manifest)
    print(json.dumps({"dataset": str(dataset_root), "rows": audit_rows}, indent=2))
    plot_counts(dataset_manifest, audit_rows)
    plot_examples(dataset_root, audit_documents)

