"""Read the original Mini Metro research archive without unsafe extraction."""

from __future__ import annotations

import io
import xml.etree.ElementTree as ET
import zipfile
from collections import Counter
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Iterator

import numpy as np
from PIL import Image


ROOT = "mini-metro-master"
CONTEXT_ROOT = f"{ROOT}/ml_train/context_classifier/data"
STATION_ROOT = f"{ROOT}/ml_train/station_detector/tensorflow/data"


@dataclass(frozen=True, slots=True)
class Box:
    xmin: float
    ymin: float
    xmax: float
    ymax: float
    label: str = "station"

    @property
    def center(self) -> tuple[float, float]:
        return ((self.xmin + self.xmax) / 2.0, (self.ymin + self.ymax) / 2.0)


@dataclass(frozen=True, slots=True)
class ContextSample:
    member: str
    split: str
    label: str


@dataclass(frozen=True, slots=True)
class StationSample:
    image_member: str
    annotation_member: str
    split: str
    width: int
    height: int
    boxes: tuple[Box, ...]


class LegacyArchive:
    def __init__(self, path: str | Path):
        self.path = Path(path)
        if not self.path.is_file():
            raise FileNotFoundError(self.path)

    def context_samples(self, split: str | None = None) -> list[ContextSample]:
        prefix = f"{CONTEXT_ROOT}/"
        result: list[ContextSample] = []
        with zipfile.ZipFile(self.path) as archive:
            for name in archive.namelist():
                if not name.startswith(prefix) or not name.lower().endswith(".png"):
                    continue
                parts = PurePosixPath(name).parts
                if len(parts) < 3:
                    continue
                sample_split, label = parts[-3], parts[-2]
                if sample_split not in {"train", "test"}:
                    continue
                if split is None or split == sample_split:
                    result.append(ContextSample(name, sample_split, label))
        return sorted(result, key=lambda sample: sample.member)

    def station_samples(self, split: str | None = None) -> list[StationSample]:
        prefix = f"{STATION_ROOT}/"
        result: list[StationSample] = []
        with zipfile.ZipFile(self.path) as archive:
            names = set(archive.namelist())
            for name in sorted(names):
                if not name.startswith(prefix) or not name.lower().endswith(".xml"):
                    continue
                parts = PurePosixPath(name).parts
                sample_split = parts[-2]
                if sample_split not in {"train", "test"}:
                    continue
                if split is not None and split != sample_split:
                    continue
                root = ET.fromstring(archive.read(name))
                filename = root.findtext("filename")
                if not filename:
                    continue
                image_member = str(PurePosixPath(name).parent / filename)
                if image_member not in names:
                    continue
                size = root.find("size")
                if size is None:
                    continue
                boxes: list[Box] = []
                for item in root.findall("object"):
                    bounds = item.find("bndbox")
                    if bounds is None:
                        continue
                    boxes.append(
                        Box(
                            float(bounds.findtext("xmin", "0")),
                            float(bounds.findtext("ymin", "0")),
                            float(bounds.findtext("xmax", "0")),
                            float(bounds.findtext("ymax", "0")),
                            item.findtext("name", "station"),
                        )
                    )
                result.append(
                    StationSample(
                        image_member,
                        name,
                        sample_split,
                        int(size.findtext("width", "0")),
                        int(size.findtext("height", "0")),
                        tuple(boxes),
                    )
                )
        return result

    def read_image(self, member: str) -> np.ndarray:
        with zipfile.ZipFile(self.path) as archive:
            with Image.open(io.BytesIO(archive.read(member))) as image:
                return np.asarray(image.convert("RGB"), dtype=np.uint8)

    def iter_context_images(
        self, split: str
    ) -> Iterator[tuple[ContextSample, np.ndarray]]:
        with zipfile.ZipFile(self.path) as archive:
            for sample in self.context_samples(split):
                with Image.open(io.BytesIO(archive.read(sample.member))) as image:
                    yield sample, np.asarray(image.convert("RGB"), dtype=np.uint8)

    def audit(self) -> dict[str, object]:
        contexts = self.context_samples()
        stations = self.station_samples()
        return {
            "archive": str(self.path),
            "context_images": len(contexts),
            "context_by_split": dict(Counter(item.split for item in contexts)),
            "context_by_label": dict(Counter(item.label for item in contexts)),
            "station_images": len(stations),
            "station_by_split": dict(Counter(item.split for item in stations)),
            "station_boxes": sum(len(item.boxes) for item in stations),
            "station_by_label": dict(
                Counter(box.label for item in stations for box in item.boxes)
            ),
        }
